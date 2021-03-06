﻿//
// Communicator.cs
//
// Author: Kees van Spelde <sicos2002@hotmail.com>
//
// Copyright (c) 2017-2021 Magic-Sessions. (www.magic-sessions.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NON INFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ChromeHtmlToPdfLib.Exceptions;
using ChromeHtmlToPdfLib.Helpers;
using ChromeHtmlToPdfLib.Protocol;
using ChromeHtmlToPdfLib.Protocol.Network;
using ChromeHtmlToPdfLib.Settings;
using Microsoft.Extensions.Logging;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable AccessToDisposedClosure
// ReSharper disable AccessToModifiedClosure

namespace ChromeHtmlToPdfLib
{
    /// <summary>
    ///     Handles all the communication tasks with Chrome remote dev tools
    /// </summary>
    /// <remarks>
    ///     See https://chromedevtools.github.io/devtools-protocol/
    /// </remarks>
    internal class Browser : IDisposable
    {
        #region Fields
        /// <summary>
        ///     Used to make the logging thread safe
        /// </summary>
        private readonly object _lock = new object();
        
        /// <summary>
        ///     When set then logging is written to this ILogger instance
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        ///      When enabled network traffic is also logged
        /// </summary>
        private readonly bool _logNetworkTraffic;

        /// <summary>
        ///     A connection to the browser (Chrome)
        /// </summary>
        private readonly Connection _browserConnection;

        /// <summary>
        ///     A connection to a page
        /// </summary>
        private readonly Connection _pageConnection;
        #endregion

        #region Properties
        /// <summary>
        ///     An unique id that can be used to identify the logging of the converter when
        ///     calling the code from multiple threads and writing all the logging to the same file
        /// </summary>
        public string InstanceId { get; set; }
        #endregion

        #region Constructor & destructor
        /// <summary>
        ///     Makes this object and sets the Chrome remote debugging url
        /// </summary>
        /// <param name="browser">The websocket to the browser</param>
        /// <param name="logger">When set then logging is written to this ILogger instance for all conversions at the Information log level</param>
        /// <param name="logNetworkTraffic">When enabled network traffic is also logged</param>
        internal Browser(Uri browser, ILogger logger, bool logNetworkTraffic)
        {
            _logger = logger;
            _logNetworkTraffic = logNetworkTraffic;

            // Open a websocket to the browser
            _browserConnection = new Connection(browser.ToString());
            
            var message = new Message {Method = "Target.createTarget"};
            message.Parameters.Add("url", "about:blank");

            var result = _browserConnection.SendAsync(message).GetAwaiter().GetResult();
            var page = Protocol.Page.Page.FromJson(result);
            var pageUrl = $"{browser.Scheme}://{browser.Host}:{browser.Port}/devtools/page/{page.Result.TargetId}";

            _pageConnection = new Connection(pageUrl);

            if (logNetworkTraffic)
            {
                var networkMessage = new Message {Method = "Network.enable"};
                _pageConnection.SendAsync(networkMessage).GetAwaiter().GetResult();
                _pageConnection.MessageReceived += _pageConnection_MessageReceived;
            }
        }
        #endregion

        #region Network traffic
        /// <summary>
        ///     Event used to log the network traffic when <see cref="_logNetworkTraffic"/> is <c>true</c>
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="data"></param>
        private void _pageConnection_MessageReceived(object sender, string data)
        {
            var message = Base.FromJson(data);

            switch (message.Method)
            {
                case "Network.requestWillBeSent":
                    var requestWillBeSent = RequestWillBeSent.FromJson(data);
                    WriteToLog($"Request sent with request id '{requestWillBeSent.Params.RequestId}' " +
                               $"for url '{requestWillBeSent.Params.Request.Url}' " +
                               $"with method '{requestWillBeSent.Params.Request.Method}' " +
                               $"and type '{requestWillBeSent.Params.Type}'");
                    break;

                case "Network.requestWillBeSentExtraInfo":
                    // Experimental
                    break;

                case "Network.dataReceived":
                    var dataReceived = DataReceived.FromJson(data);
                    WriteToLog($"Data received for request id '{dataReceived.Params.RequestId}' " +
                               $"with length '{dataReceived.Params.DataLength}'");
                    break;

                case "Network.responseReceived":
                    var responseReceived = ResponseReceived.FromJson(data);
                    var logMessage = $"Response received for request id '{responseReceived.Params.RequestId}' " +
                                     $"and url '{responseReceived.Params.Response.Url}'";

                    if (!string.IsNullOrWhiteSpace(responseReceived.Params.Response.RemoteIpAddress))
                        logMessage += $" from ip '{responseReceived.Params.Response.RemoteIpAddress}' " +
                                      $"on port '{responseReceived.Params.Response.RemotePort}' " +
                                      $"with status '{responseReceived.Params.Response.Status}'";

                    WriteToLog(logMessage);
                        
                    break;

                case "Network.responseReceivedExtraInfo":
                    // Experimental
                    break;

                case "Network.loadingFinished":
                    var loadingFinished = LoadingFinished.FromJson(data);
                    WriteToLog($"Loading finished for request id '{loadingFinished.Params.RequestId}' " +
                               $"with encoded data length '{loadingFinished.Params.EncodedDataLength}'");
                    break;

                case "Network.loadingFailed":
                    var loadingFailed = LoadingFailed.FromJson(data);
                    WriteToLog($"Loading failed for request id '{loadingFailed.Params.RequestId}' " +
                               $"and type '{loadingFailed.Params.Type}' " +
                               $"with error '{loadingFailed.Params.ErrorText}'");
                    break;
            }
        }
        #endregion

        #region NavigateTo
        /// <summary>
        ///     Instructs Chrome to navigate to the given <paramref name="uri" />
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="countdownTimer">If a <see cref="CountdownTimer"/> is set then
        ///     the method will raise an <see cref="ConversionTimedOutException"/> if the 
        ///     <see cref="CountdownTimer"/> reaches zero before finishing navigation</param>
        /// <param name="mediaLoadTimeout">When set a timeout will be started after the DomContentLoaded
        ///     event has fired. After a timeout the NavigateTo method will exit as if the page
        ///     has been completely loaded</param>
        /// <param name="urlBlacklist">A list with URL's that need to be blocked (use * as a wildcard)</param>
        /// <param name="safeUrls">A list with URL's that are safe to load</param>
        /// <exception cref="ChromeException">Raised when an error is returned by Chrome</exception>
        /// <exception cref="ConversionTimedOutException">Raised when <paramref name="countdownTimer"/> reaches zero</exception>
        public void NavigateTo(
            Uri uri,
            List<string> safeUrls,
            CountdownTimer countdownTimer = null,
            int? mediaLoadTimeout = null,
            List<string> urlBlacklist = null)
        {
            var waitEvent = new ManualResetEvent(false);
            var mediaLoadTimeoutCancellationTokenSource = new CancellationTokenSource();
            var navigationError = string.Empty;
            var waitForNetworkIdle = false;
            var mediaTimeoutTaskSet = false;

            var messageHandler = new EventHandler<string>(delegate(object sender, string data)
            {
                //System.IO.File.AppendAllText("d:\\logs.txt", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff") + " - " + data + Environment.NewLine);
                var message = Message.FromJson(data);

                switch (message.Method)
                {
                    case "Fetch.requestPaused":
                    {
                        var fetch = Fetch.FromJson(data);
                        var requestId = fetch.Params.RequestId;
                        var url = fetch.Params.Request.Url;
                        var isSafeUrl = safeUrls.Contains(url);

                        if (!RegularExpression.IsRegExMatch(urlBlacklist, url, out var matchedPattern) || isSafeUrl)
                        {
                            WriteToLog($"The url '{url}' has been allowed{(isSafeUrl ? " because it is on the safe url list" : string.Empty)}");
                            var fetchContinue = new Message {Method = "Fetch.continueRequest"};
                            fetchContinue.Parameters.Add("requestId", requestId);
                            _pageConnection.SendAsync(fetchContinue).GetAwaiter();
                        }
                        else
                        {
                            WriteToLog($"The url '{url}' has been blocked by url blacklist pattern '{matchedPattern}'");

                            var fetchFail = new Message {Method = "Fetch.failRequest"};
                            fetchFail.Parameters.Add("requestId", requestId);

                            // Failed, Aborted, TimedOut, AccessDenied, ConnectionClosed, ConnectionReset, ConnectionRefused,
                            // ConnectionAborted, ConnectionFailed, NameNotResolved, InternetDisconnected, AddressUnreachable,
                            // BlockedByClient, BlockedByResponse
                            fetchFail.Parameters.Add("errorReason", "BlockedByClient");
                            _pageConnection.SendAsync(fetchFail).GetAwaiter();
                        }

                        break;
                    }

                    default:
                    {
                        var page = Protocol.Page.Event.FromJson(data);

                        switch (page.Method)
                        {
                            // The DOMContentLoaded event is fired when the document has been completely loaded and parsed, without
                            // waiting for stylesheets, images, and sub frames to finish loading (the load event can be used to
                            // detect a fully-loaded page).
                            case "Page.lifecycleEvent" when page.Params?.Name == "DOMContentLoaded":
                                if (mediaLoadTimeout.HasValue && !mediaTimeoutTaskSet)
                                {
                                    try
                                    {
                                        Task.Run(async delegate
                                        {
                                            await Task.Delay(mediaLoadTimeout.Value, mediaLoadTimeoutCancellationTokenSource.Token);
                                            WriteToLog($"Media load timed out after {mediaLoadTimeout.Value} milliseconds");
                                            waitEvent?.Set();
                                        }, mediaLoadTimeoutCancellationTokenSource.Token);

                                        mediaTimeoutTaskSet = true;
                                    }
                                    catch 
                                    {
                                        // Ignore
                                    }
                                }

                                break;

                            case "Page.frameNavigated":
                                WriteToLog("The 'Page.frameNavigated' event has been fired, waiting for the 'Page.lifecycleEvent' with name 'networkIdle'");
                                waitForNetworkIdle = true;
                                break;

                            case "Page.lifecycleEvent" when page.Params?.Name == "networkIdle" && waitForNetworkIdle:
                                WriteToLog("The 'Page.lifecycleEvent' event with name 'networkIdle' has been fired, the page is now fully loaded");
                                waitEvent?.Set();
                                break;

                            default:
                                var pageNavigateResponse = Protocol.Page.NavigateResponse.FromJson(data);
                                if (!string.IsNullOrEmpty(pageNavigateResponse.Result?.ErrorText) &&
                                    !pageNavigateResponse.Result.ErrorText.Contains("net::ERR_BLOCKED_BY_CLIENT"))
                                {
                                    navigationError = $"{pageNavigateResponse.Result.ErrorText} occurred when navigating to the page '{uri}'";
                                    waitEvent?.Set();
                                }

                                break;
                        }

                        break;
                    }
                }
            });

            _pageConnection.MessageReceived += messageHandler;
            _pageConnection.Closed += (sender, args) => waitEvent?.Set();

            // Enable Fetch when we want to blacklist certain URL's
            if (urlBlacklist?.Count > 0)
            {
                WriteToLog("Enabling Fetch to block url's that are in the url blacklist'");
                _pageConnection.SendAsync(new Message {Method = "Fetch.enable"}).GetAwaiter();
            }

            _pageConnection.SendAsync(new Message {Method = "Page.enable"}).GetAwaiter();

            var lifecycleEventEnabledMessage = new Message {Method = "Page.setLifecycleEventsEnabled"};
            lifecycleEventEnabledMessage.AddParameter("enabled", true);
            _pageConnection.SendAsync(lifecycleEventEnabledMessage).GetAwaiter();

            var pageNavigateMessage = new Message {Method = "Page.navigate"};
            pageNavigateMessage.AddParameter("url", uri.ToString());

            _pageConnection.SendAsync(pageNavigateMessage).GetAwaiter();

            if (countdownTimer != null)
            {
                waitEvent.WaitOne(countdownTimer.MillisecondsLeft);
                if (countdownTimer.MillisecondsLeft == 0)
                    throw new ConversionTimedOutException($"The {nameof(NavigateTo)} method timed out");
            }
            else
                waitEvent.WaitOne();

            _pageConnection.MessageReceived -= messageHandler;

            if (mediaTimeoutTaskSet)
            {
                mediaLoadTimeoutCancellationTokenSource.Cancel();
                mediaLoadTimeoutCancellationTokenSource.Dispose();
            }

            var lifecycleEventDisabledMessage = new Message {Method = "Page.setLifecycleEventsEnabled"};
            lifecycleEventDisabledMessage.AddParameter("enabled", false);

            _pageConnection.SendAsync(lifecycleEventDisabledMessage).GetAwaiter();
            _pageConnection.SendAsync(new Message {Method = "Page.disable"}).GetAwaiter();

            // Disable Fetch again if it was enabled
            if (urlBlacklist?.Count > 0)
                _pageConnection.SendAsync(new Message {Method = "Fetch.disable"}).GetAwaiter();

            waitEvent.Dispose();
            waitEvent = null;

            if (!string.IsNullOrEmpty(navigationError))
            {
                WriteToLog(navigationError);
                throw new ChromeNavigationException(navigationError);
            }
        }
        #endregion

        #region WaitForWindowStatus
        /// <summary>
        ///     Wait until the javascript window.status is returning the given <paramref name="status" />
        /// </summary>
        /// <param name="status">The case insensitive status</param>
        /// <param name="timeout">Continue after reaching the set timeout in milliseconds</param>
        /// <returns><c>true</c> when window status matched, <c>false</c> when timing out</returns>
        /// <exception cref="ChromeException">Raised when an error is returned by Chrome</exception>
        public bool WaitForWindowStatus(string status, int timeout = 60000)
        {
            var message = new Message {Method = "Runtime.evaluate"};
            message.AddParameter("expression", "window.status;");
            message.AddParameter("silent", true);
            message.AddParameter("returnByValue", true);

            var waitEvent = new ManualResetEvent(false);
            var match = false;

            void MessageReceived(object sender, string data)
            {
                var evaluate = Evaluate.FromJson(data);
                if (evaluate.Result?.Result?.Value != status) return;
                match = true;
                waitEvent.Set();
            }

            _pageConnection.MessageReceived += MessageReceived;

            var stopWatch = new Stopwatch();
            stopWatch.Start();

            while (!match)
            {
                _pageConnection.SendAsync(message).GetAwaiter();
                waitEvent.WaitOne(10);
                if (stopWatch.ElapsedMilliseconds >= timeout) break;
            }

            stopWatch.Stop();
            _pageConnection.MessageReceived -= MessageReceived;

            return match;
        }
        #endregion
        
        #region RunJavascript
        /// <summary>
        ///     Runs the given javascript after the page has been fully loaded
        /// </summary>
        /// <param name="script">The javascript to run</param>
        /// <exception cref="ChromeException">Raised when an error is returned by Chrome</exception>
        public void RunJavascript(string script)
        {
            var message = new Message {Method = "Runtime.evaluate"};
            message.AddParameter("expression", script);
            message.AddParameter("silent", false);
            message.AddParameter("returnByValue", false);

            var errorDescription = string.Empty;

            void MessageReceived(object sender, string data)
            {
                var evaluateError = EvaluateError.FromJson(data);

                if (evaluateError.Result?.ExceptionDetails != null)
                    errorDescription = evaluateError.Result.ExceptionDetails.Exception.Description;
            }

            _pageConnection.MessageReceived += MessageReceived;
            _pageConnection.SendAsync(message).GetAwaiter().GetResult();
            _pageConnection.MessageReceived -= MessageReceived;

            if (!string.IsNullOrEmpty(errorDescription))
                throw new ChromeException(errorDescription);
        }
        #endregion

        #region CaptureSnapshot
        /// <summary>
        ///     Instructs Chrome to capture a snapshot from the loaded page
        /// </summary>
        /// <param name="countdownTimer">If a <see cref="CountdownTimer"/> is set then
        /// the method will raise an <see cref="ConversionTimedOutException"/> in the 
        /// <see cref="CountdownTimer"/> reaches zero before finishing the printing to pdf</param>        
        /// <remarks>
        ///     See https://chromedevtools.github.io/devtools-protocol/tot/Page#method-captureSnapshot
        /// </remarks>
        /// <returns></returns>
        internal async Task<SnapshotResponse> CaptureSnapshot(CountdownTimer countdownTimer = null)
        {
            var message = new Message { Method = "Page.captureSnapshot" };

            var result = countdownTimer == null
                ? await _pageConnection.SendAsync(message)
                : await _pageConnection.SendAsync(message).Timeout(countdownTimer.MillisecondsLeft);

            return SnapshotResponse.FromJson(result);
        }
        #endregion

        #region PrintToPdf
        /// <summary>
        ///     Instructs Chrome to print the page
        /// </summary>
        /// <param name="pageSettings"><see cref="PageSettings" /></param>
        /// <param name="countdownTimer">If a <see cref="CountdownTimer"/> is set then
        /// the method will raise an <see cref="ConversionTimedOutException"/> in the 
        /// <see cref="CountdownTimer"/> reaches zero before finishing the printing to pdf</param>        
        /// <remarks>
        ///     See https://chromedevtools.github.io/devtools-protocol/tot/Page/#method-printToPDF
        /// </remarks>
        /// <exception cref="ConversionException">Raised when Chrome returns an empty string</exception>
        /// <exception cref="ConversionTimedOutException">Raised when <paramref name="countdownTimer"/> reaches zero</exception>
        internal async Task<PrintToPdfResponse> PrintToPdf(
            PageSettings pageSettings, 
            CountdownTimer countdownTimer = null)
        {
            var message = new Message {Method = "Page.printToPDF"};
            message.AddParameter("landscape", pageSettings.Landscape);
            message.AddParameter("displayHeaderFooter", pageSettings.DisplayHeaderFooter);
            message.AddParameter("printBackground", pageSettings.PrintBackground);
            message.AddParameter("scale", pageSettings.Scale);
            message.AddParameter("paperWidth", pageSettings.PaperWidth);
            message.AddParameter("paperHeight", pageSettings.PaperHeight);
            message.AddParameter("marginTop", pageSettings.MarginTop);
            message.AddParameter("marginBottom", pageSettings.MarginBottom);
            message.AddParameter("marginLeft", pageSettings.MarginLeft);
            message.AddParameter("marginRight", pageSettings.MarginRight);
            message.AddParameter("pageRanges", pageSettings.PageRanges ?? string.Empty);
            message.AddParameter("ignoreInvalidPageRanges", pageSettings.IgnoreInvalidPageRanges);
            if (!string.IsNullOrEmpty(pageSettings.HeaderTemplate))
                message.AddParameter("headerTemplate", pageSettings.HeaderTemplate);
            if (!string.IsNullOrEmpty(pageSettings.FooterTemplate))
                message.AddParameter("footerTemplate", pageSettings.FooterTemplate);
            message.AddParameter("preferCSSPageSize", pageSettings.PreferCSSPageSize);

            var result = countdownTimer == null
                ? await _pageConnection.SendAsync(message)
                : await _pageConnection.SendAsync(message).Timeout(countdownTimer.MillisecondsLeft);

            var printToPdfResponse = PrintToPdfResponse.FromJson(result);

            if (string.IsNullOrEmpty(printToPdfResponse.Result?.Data))
                throw new ConversionException("Conversion failed");

            return printToPdfResponse;
        }
        #endregion

        #region CaptureScreenshot
        /// <summary>
        ///     Instructs Chrome to take a screenshot of the page
        /// </summary>
        /// <exception cref="ConversionException">Raised when Chrome returns an empty string</exception>
        /// <exception cref="ConversionTimedOutException">Raised when <paramref name="countdownTimer"/> reaches zero</exception>
        internal async Task<CaptureScreenshotResponse> CaptureScreenshot(CountdownTimer countdownTimer = null)
        {
            var message = new Message { Method = "Page.captureScreenshot" };
            var result = countdownTimer == null
                ? await _pageConnection.SendAsync(message)
                : await _pageConnection.SendAsync(message).Timeout(countdownTimer.MillisecondsLeft);

            var captureScreenshotResponse = CaptureScreenshotResponse.FromJson(result);

            if (string.IsNullOrEmpty(captureScreenshotResponse.Result?.Data))
                throw new ConversionException("Screenshot capture failed");

            return captureScreenshotResponse;
        }
        #endregion

        #region Close
        /// <summary>
        ///     Instructs Chrome to close
        /// </summary>
        /// <param name="countdownTimer">If a <see cref="CountdownTimer"/> is set then
        /// the method will raise an <see cref="ConversionTimedOutException"/> in the 
        /// <see cref="CountdownTimer"/> reaches zero before Chrome responses that it is going to close</param>    
        /// <exception cref="ChromeException">Raised when an error is returned by Chrome</exception>
        public void Close(CountdownTimer countdownTimer = null)
        {
            var message = new Message {Method = "Browser.close"};

            if (countdownTimer != null)
                _browserConnection.SendAsync(message).Timeout(countdownTimer.MillisecondsLeft).GetAwaiter();
            else
                _browserConnection.SendAsync(message).GetAwaiter();
        }
        #endregion

        #region WriteToLog
        /// <summary>
        ///     Writes a line to the <see cref="_logger" />
        /// </summary>
        /// <param name="message">The message to write</param>
        internal void WriteToLog(string message)
        {
            lock (_lock)
            {
                try
                {
                    if (_logger == null) return;
                    using (_logger.BeginScope(InstanceId))
                        _logger.LogInformation(message);
                }
                catch (ObjectDisposedException)
                {
                    // Ignore
                }
            }
        }
        #endregion

        #region Dispose
        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            if (_logNetworkTraffic)
            {
                var networkMessage = new Message {Method = "Network.disable"};
                _pageConnection.SendAsync(networkMessage).GetAwaiter().GetResult();
                _pageConnection.MessageReceived -= _pageConnection_MessageReceived;
            }

            _pageConnection?.Dispose();
            _browserConnection?.Dispose();
        }
        #endregion
    }
}