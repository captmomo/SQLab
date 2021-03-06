﻿using System;
using Microsoft.Extensions.Logging;
using SqCommon;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace SQLab
{
    internal class SQLabAspLoggerProvider : ILoggerProvider
    {
        private bool m_disposed = false;

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string p_categoryName)
        {
            Microsoft.Extensions.Logging.ILogger aspLogger = new SQLabCommonAspLogger(p_categoryName);
            return aspLogger;
        }

        //official Disposable pattern. The finalizer should call your dispose method explicitly.
        //Note:!! The finalizer isn't guaranteed to be called if your application hard crashes.
        //Checked: when I shutdown the Webserver, by typing Ctrl-C, saying Initiate Webserver shutdown, this Dispose was called by an External code; maybe Webserver, maybe the GC. 
        // So, it means that at official Webserver shutdown (change of web.config, shutting down AzureVM, etc.), this Dispose is called.
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool p_disposing)
        {
            if (p_disposing)
            {
                // dispose managed resources
                if (!m_disposed)
                {
                    m_disposed = true;
                    //m_nLogFactory.Flush();  // for sending all the Logs to TableStorage or to the logger EmailBufferingWrapper
                    //m_nLogFactory.Dispose();
                }
            }
            // dispose unmanaged resources
        }

        ~SQLabAspLoggerProvider()
        {
            this.Dispose(false);
        }
    }

    internal class SQLabCommonAspLogger : Microsoft.Extensions.Logging.ILogger, IDisposable
    {
        private string p_categoryName;
        object m_scope;

        public SQLabCommonAspLogger(string p_categoryName)
        {
            this.p_categoryName = p_categoryName;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }
            m_scope = state;
            return this;
        }

        // Gets a value indicating whether logging is enabled for the specified level.
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
        {
            switch (logLevel)
            {
                case LogLevel.Critical: return true;
                case LogLevel.Error: return true;
                case LogLevel.Warning: return true;
                case LogLevel.Information: return true;
                case LogLevel.Trace: return false;
                default: return false;
            }
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var message = string.Empty;
            if (formatter != null)
            {
                try
                {
                    message = formatter(state, exception);  // this is Microsoft.AspNetCore.Hosting.Internal.HostingLoggerExtensions.HostingRequestStarting.ToString(). it can give exception: "System.ArgumentException: Decoded string is not a valid IDN name."
                }
                catch (Exception e) // "System.ArgumentException: Decoded string is not a valid IDN name." at Microsoft.AspNetCore.Http.HostString.ToUriComponent()
                {
                    // swallow the exception and substitute Message with the Exception data.
                    message = "SQLabAspLoggerProvider.Log(): Log Message could'n be formatted to ASCII by the Formatter. Probably bad URL input query. Investigate log files about the URL query. Exception is this: " + e.ToString();
                }
                //if (exception != null)  // formatter function doesn't put the Exception into the message, so add it.
                //{
                //    message += Environment.NewLine + exception;
                //}
            }
            else
            {
                if (state != null)
                {
                    message += state;
                }
                if (exception != null)
                {
                    message += Environment.NewLine + exception;
                }
            }
            if (!string.IsNullOrEmpty(message))
            {
                switch (logLevel)
                {
                    case LogLevel.Critical:
                        if (exception == null)
                            Utils.Logger.Fatal(message);
                        else
                            Utils.Logger.Fatal(exception, message);
                        break;
                    case LogLevel.Error:
                        if (exception == null)
                            Utils.Logger.Error(message);
                        else
                            Utils.Logger.Error(exception, message);
                        break;
                    case LogLevel.Warning:
                        if (exception == null)
                            Utils.Logger.Warn(message);
                        else
                            Utils.Logger.Warn(exception, message);
                        break;
                    case LogLevel.Information:
                        Utils.Logger.Info(message);
                        break;
                    case LogLevel.Trace:
                        Utils.Logger.Debug(message);
                        break;
                    default:
                        Utils.Logger.Debug(message);
                        break;
                }


                //_traceSource.TraceEvent(GetEventType(logLevel), eventId.Id, message);
            }

        }

        public void Dispose()
        {
        }
    }

}