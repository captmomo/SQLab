/* Copyright (C) 2013 Interactive Brokers LLC. All rights reserved.  This code is subject to the terms
 * and conditions of the IB API Non-Commercial License or the IB API Commercial License, as applicable. */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using IBApi;
using SqCommon;
using System.Collections.Concurrent;
using System.Threading;
using System.Globalization;
using DbCommon;
using Utils = SqCommon.Utils;
using System.Diagnostics;

namespace VirtualBroker
{

    public class BrokerWrapperIb : IBrokerWrapper
    {
        GatewayUser m_gatewayUser;
        EClientSocket clientSocket;
        public readonly EReaderSignal Signal = new EReaderMonitorSignal();
        EReader m_eReader;
        private int nextOrderId;

        int m_reqMktDataIDseed = 1000;
        protected int GetUniqueReqMktDataID
        {
            get { return Interlocked.Increment(ref m_reqMktDataIDseed); }  // Increment gives back the incremented value, not the old value
        }

        int m_reqHistoricalDataIDseed = 1000;
        protected int GetUniqueReqHistoricalDataID
        {
            get { return Interlocked.Increment(ref m_reqHistoricalDataIDseed); }  // Increment gives back the incremented value, not the old value
        }

        int m_reqAccountSumIDseed = 1000;
        protected int GetUniqueReqAccountSumID
        {
            get { return Interlocked.Increment(ref m_reqAccountSumIDseed); }  // Increment gives back the incremented value, not the old value
        }

        public string IbAccountsList { get; set; }
        public ConcurrentDictionary<int, MktDataSubscription> MktDataSubscriptions { get; set; } = new ConcurrentDictionary<int, MktDataSubscription>();
        public ConcurrentDictionary<int, MktDataSubscription> CancelledMktDataSubscriptions { get; set; } = new ConcurrentDictionary<int, MktDataSubscription>(); // we keep it as a log, however we remove the price parts to not consume memory
        public ConcurrentDictionary<int, HistDataSubscription> HistDataSubscriptions { get; set; } = new ConcurrentDictionary<int, HistDataSubscription>();
        public ConcurrentDictionary<int, OrderSubscription> OrderSubscriptions { get; set; } = new ConcurrentDictionary<int, OrderSubscription>();

        public delegate void AccSumArrivedFunc(int p_reqId, string p_tag, string p_value, string p_currency);
        public AccSumArrivedFunc m_accSumArrCb;
        public delegate void AccSumEndFunc(int p_reqId);
        public AccSumEndFunc m_accSumEndCb;

        public delegate void AccPosArrivedFunc(string p_account, Contract p_contract, double p_pos, double p_avgCost);
        public AccPosArrivedFunc m_accPosArrCb;
        public delegate void AccPosEndFunc();
        public AccPosEndFunc m_accPosEndCb;

        public EClientSocket ClientSocket
        {
            get { return clientSocket; }
            set { clientSocket = value; }
        }

        public int NextOrderId
        {
            get { return nextOrderId; }
            set { nextOrderId = value; }
        }


        public BrokerWrapperIb()
        {
            clientSocket = new EClientSocket(this, Signal);
        }

        public BrokerWrapperIb(AccSumArrivedFunc p_accSumArrCb, AccSumEndFunc p_accSumEndCb, AccPosArrivedFunc p_accPosArrCb, AccPosEndFunc p_accPosEndCb) : this()
        {
            m_accSumArrCb = p_accSumArrCb;
            m_accSumEndCb = p_accSumEndCb;
            m_accPosArrCb = p_accPosArrCb;
            m_accPosEndCb = p_accPosEndCb;
        }


        public virtual bool Connect(GatewayUser p_gatewayUser, int p_socketPort, int p_brokerConnectionClientID)
        {
            m_gatewayUser = p_gatewayUser;
            Utils.Logger.Info($"ClientSocket.eConnect(127.0.0.1, {p_socketPort}, {p_brokerConnectionClientID}, false)");
            ClientSocket.eConnect("127.0.0.1", p_socketPort, p_brokerConnectionClientID, false);
            //Create a reader to consume messages from the TWS. The EReader will consume the incoming messages and put them in a queue
            m_eReader = new EReader(ClientSocket, Signal);
            m_eReader.Start();
            //Once the messages are in the queue, an additional thread need to fetch them. This is a very long running Thread, always waiting for all messages (Price, historicalData, etc.). This Thread calls the IbWrapper Callbacks.
            new Thread(() =>
            {
                try
                {
                    while (ClientSocket.IsConnected())
                    {
                        Signal.waitForSignal(); // the reader thread will sign the Signal
                        m_eReader.processMsgs();
                    }
                }
                catch (Exception e)
                {
                    if (Utils.MainThreadIsExiting.IsSet)
                        return; // if App is exiting gracefully, this Exception is not a problem
                    Utils.Logger.Error("Exception caught in Gateway Thread that is fetching messages. " + e.Message + " ,InnerException: " + ((e.InnerException != null) ? e.InnerException.Message : ""));
                    throw;  // else, rethrow. This will Crash the App, which is OK. Without IB connection, there is no point to continue the VBroker App.
                }
            })
            { IsBackground = true }.Start();

            /*************************************************************************************************************************************************/
            /* One (although primitive) way of knowing if we can proceed is by monitoring the order's nextValidId reception which comes down automatically after connecting. */
            /*************************************************************************************************************************************************/
            //This is returned at Connection:
            //Account list: U1****6
            //Next Valid Id: 1
            DateTime startWaitConnection = DateTime.UtcNow;
            while (NextOrderId <= 0)
            {
                Thread.Sleep(100);
                if ((DateTime.UtcNow - startWaitConnection).TotalSeconds > 5.0)
                {
                    return false;
                }
            }
            return true;
        }



        public bool IsConnected()
        {
            return ClientSocket.IsConnected();
        }

        public virtual void Disconnect()
        {
            Disconnect(true);
        }
        public virtual void Disconnect(bool p_isIbConnectionAlive = true)
        {
            if (p_isIbConnectionAlive)
            {
                foreach (var item in MktDataSubscriptions)
                {
                    if (!item.Value.IsSnapshot)
                        ClientSocket.cancelMktData(item.Key);
                }
            }
            m_eReader.Stop();
            ClientSocket.eDisconnect();
        }

        public virtual void connectionClosed()
        {
            //Notifes when the API-TWS connectivity has been closed. via call to ClientSocket.eDisconnect();
            Utils.Logger.Info($"BrokerWrapperIb.connectionClosed() callback: {m_gatewayUser}");
            Console.WriteLine($"*{DateTime.UtcNow.ToString("dd'T'HH':'mm':'ss")}: Warning! IB Connection Closed: {m_gatewayUser}.");
        }

        // Exception thrown: System.IO.EndOfStreamException: Unable to read beyond the end of the stream.     (if IBGateways are crashing down.)
        public virtual void error(Exception e)
        {
            //Console.WriteLine("BrokerWrapperIb.error(). Exception: " + e);    // Utils.Logger.Error() will write to console anyway
            Utils.Logger.Info("BrokerWrapperIb.error(). Exception: " + e);  // exception.ToString() writes the Stacktrace, not only the Message, which is OK.

            bool isExpectedException = false;
            // Expect no connection at ManualTraderServer, don't clutter the Console, but save it to log file.
            if (e.Message.StartsWith("One or more errors occurred. (No connection could be made because the target machine actively refused it") ||     // on Windows local development
                (e.Message.IndexOf("Connection refused") != -1))   // on Linux, ManualTraderServer, after IB TWS auto-exited and no longer exist.
                isExpectedException = true;
            if (e is System.IO.EndOfStreamException) {
                isExpectedException = true;
                // Do not reset now. We can decide to reset MktDataSubscriptions, but HistDataSubscriptions and OrderSubscriptions are better to keep, so it needs more thinking whether to discard old things. 
                // E.g. a BrokerTask will be able to get data of Finished orders, or finished historical prices, if we leave those data.
                // The downside is that if there is many Disconnect/Reconnect then MktDataSubscriptions can grow at every reconnect. But on ManualTraderServer we don't stream price data, so it is fine now.
                Disconnect(false);  // this will set ClientSocket.IsConnected to false.
            }
            
            // when VBroker server restarts every morning, the IBGateways are closed too, and as we were keeping a live TcpConnection, we will get an Exception here.
            // We cannot properly Close our connection in this case, because IBGateways are already shutting down.
            // Actually, This expected exception in the Background thread comes 5 mseconds before the ConsoleApp gets the Console.Readline() exception.
            // so in case of EndOfStreamException, let's sleep 100-500msec, and check that MainThread is exiting or not then.
            // 0405T06:15:04.481#14#5#Error: Unexpected BrokerWrapperIb.error(). Exception thrown: System.IO.EndOfStreamException: Unable to read beyond the end of the stream. at System.IO.BinaryReader.FillBuffer(Int32 numBytes)
            // 0405T06:15:04.485#1#5#Info: Console.ReadLine() Exception. Somebody closed the Terminal Window. Exception message: Input/output error
            // 0405T06: 15:04.498#1#5#Info: ****** Main() END
            // 0405T06: 15:04.534#1#5#Info: Connection closed.
            if (e is System.IO.EndOfStreamException)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(300));  // if it is a server reboot, probably during this time, the Main thread will exit anyway, which is OK, because we don't want to send Error report to HealthMonitor in that case.
                if (Utils.MainThreadIsExiting.IsSet)
                {
                    // an expected exception. Don't send Error message to HealthMonitor. This expected event happens every day.
                    Utils.Logger.Info("BrokerWrapperIb.error(). Expected exception, because 'Utils.MainThreadIsExiting.IsSet && e is System.IO.EndOfStreamException'" + e);
                    return;
                }
            }

            // if there is a Connection Error at the begginning, GatewaysWatcher will try to Reconnect 3 times. 
            // There is no point sending HealthManager.ErrorMessages and Phonecalls when the first Connection error occurs. GatewaysWatcher() will send it after the 3rd connection fails.
            // if (e is System.AggregateException)
            // {
            //     Utils.Logger.Info("BrokerWrapperIb.error(). AggregateException. Inner: " + e.InnerException.Message);
            //     if (e.InnerException.Message.IndexOf("Connection refused") != -1)
            //     {
            //         Utils.Logger.Info("BrokerWrapperIb.error().  AggregateException. Inner exception is expected. Don't raise HealthMonitor alert phonecalls (yet)");
            //         return;
            //     }
            // }

            // Otherwise, maybe a trading Error, exception.
            // Maybe IBGateways were shut down. In which case, we cannot continue this VBroker App, because we should restart IBGateways, and reconnect to them by restarting VBroker.
            // Try to send HealthMonitor message and shut down the VBroker.
            if (!isExpectedException)
            {
                // If it is not Expected exception => this thread will terminate, which will terminate the whole App. Send HealthMonitorMessage, if it is an active.
                Utils.Logger.Error("Unexpected BrokerWrapperIb.error(). Exception thrown: " + e);
                if (!Controller.IsRunningAsLocalDevelopment())
                    HealthMonitorMessage.SendAsync($"Exception in Unexpected  BrokerWrapperIb.error(). Exception: '{ e.ToStringWithShortenedStackTrace(400)}'", HealthMonitorMessageID.ReportErrorFromVirtualBroker).RunSynchronously();
                throw e;    
            }
        }

        public virtual void error(string p_str)
        {
            string errMsg = "BrokerWrapper.error(str). " + p_str;
            Console.WriteLine(errMsg);
            Utils.Logger.Error(errMsg);
            if (!Controller.IsRunningAsLocalDevelopment())
                HealthMonitorMessage.SendAsync($"Msg from BrokerWrapperIb.error(). {errMsg}", HealthMonitorMessageID.ReportErrorFromVirtualBroker).FireParallelAndForgetAndLogErrorTask();
            //If there is a single trading error, we may want to continue, so don't terminate the thread or the App, just inform HealthMonitor.
            //throw e;    // this thread will terminate. Because we don't expect this exception. Safer to terminate thread, which will terminate App. The user probably has to restart IBGateways manually anyway.
        }

        public virtual void error(int id, int errorCode, string errorMsg)
        {
            string errMsg = "Id: " + id + ", ErrCode: " + errorCode + ", Msg: " + errorMsg;
            Utils.Logger.Debug("BrokerWrapper.error(id,errCode,errMsg). " + errMsg); // even if we return and continue, Log it, so it is conserved in the log file.
            bool isAddOrderInfoToErrMsg = false;

            if (id == -1)       // -1 probably means there is no ID of the error. It is a special notation.
            {                
                if (errorCode == 0)
                {
                    // Id: -1, ErrCode: 0, Msg: Warning: Approaching max rate of 50 messages per second (45)
                    // later we need HealthMonitorMessageID.ReportWarningFromVirtualBroker too which will send only emails, but not Phonecalls
                    // for the moment, just swallow this, because it is more than a Warning, not an error yet.
                    // When it becomes an error, another message will be called, which should be handled then.
                    // Id: 1, ErrCode: 100, Msg: Max rate of messages per second has been exceeded:max=50 rec=651 (1)
                    // If that error happens, we have to investigate logs (are messages lost?), and if necessary write code to slow the query down.
                    // 2018-12: GetAccountInfoPos(): we query 113 RT prices, even without options underlyings. "Waiting for RT prices: 1297.74 ms. Queried: 113, ReceivedOk: 113, ReceivedErr: 0, Missing: 0"
                    Utils.Logger.Warn("Strong Warning. BrokerWrapper.error(id,errCode,errMsg). " + errMsg);
                    Console.WriteLine("Strong Warning. BrokerWrapper.error(id,errCode,errMsg). " + errMsg);
                    return; // skip processing the error further. Don't send it to HealthMonitor.
                }
                if (errorCode == 2104 || errorCode == 2106 || errorCode == 2107 || errorCode == 2108 || errorCode == 2119 || errorCode == 2158)
                {
                    // This is not an error. It is the messages at Connection: 
                    //IB Error. Id: -1, Code: 2104, Msg: Market data farm connection is OK:hfarm
                    //IB Error. Id: -1, Code: 2106, Msg: HMDS data farm connection is OK:ushmds.us
                    //IB Error. Id: -1, Code: 2107, Msg: HMDS data farm connection is inactive but should be available upon demand.ushmds
                    //IB Error. Id: -1, Code: 2108, Msg: HMDS data farm connection is inactive but should be available upon demand.ushmds
                    //IB Error. Id: -1, Code: 2119, Msg: Market data farm is connecting:usfarm
                    //IB Error. Id: -1, Code: 2158, Msg: Sec-def data farm connection is OK:secdefnj
                    return; // skip processing the error further. Don't send it to HealthMonitor.
                }
                if (errorCode == 2103 || errorCode == 2105 || errorCode == 1100 || errorCode == 1102)
                {
                    // This is Usually not an error if it is received pre-market or after market. IBGateway will try reconnecting, so this is usually temporary. However, log it.
                    //IB Error. ErrId: -1, ErrCode: 2103, Msg: Market data farm connection is broken:usfarm
                    //IB Error. ErrId: -1, ErrCode: 2105, Msg: HMDS data farm connection is broken:ushmds
                    //IB Error. ErrId: -1, ErrCode: 1100, Msg: Connectivity between IB and Trader Workstation has been lost.
                    //IB Error. ErrId: -1, ErrCode: 1102, Msg: Connectivity between IB and Trader Workstation has been restored - data maintained.
                    if (!GatewaysWatcher.IsApproximatelyMarketTradingTimeForIgnoringIBErrors())
                        return; // skip processing the error further. Don't send it to HealthMonitor.
                    var vbServerEnvironment = Utils.Configuration["VbServerEnvironment"];
                    if (vbServerEnvironment.ToLower() == "ManualTradingServer".ToLower())
                    {
                        return; // it can happen on MTS after market open, because TWS is there, not the IBGateway, which is less reliable. Eventually broken data connection will be restored. Skip processing the error further. Don't send it to HealthMonitor.
                    }

                    // otherwise, during market hours, consider this as an error, => so HealthMonitor will be notified
                }
            }


            if (errorCode == 100)
            {
                // IB pacing restriction. TWS:  50 requests / sec, IB gateway: ~120 requests / sec
                // ErrId: 1, ErrCode: 100, Msg: Max rate of messages per second has been exceeded:max=50 rec=651 (1)
                // ErrId: 2, ErrCode: 100, Msg: Max rate of messages per second has been exceeded:max=50 rec=182 (1)
                // "Max rate of messages per second has been exceeded.	The client application has exceeded the rate of 50 messages/second. 
                // The TWS will likely disconnect the client application after this message. "
                // <2018-12: it didn't disconnect TWS, but later I might not have RT prices for PINK stocks>
                // think about what to do here, but in general, Admins should be informed about it by email or phonecall, and we have to program a workaround, so this doesn't happen, so IB doesn't disconnect

                // https://groups.io/g/twsapi/topic/4047779
                //Instead of 50msgs per second, if you connect via IB Gateway instead of TWS, you will be able to make about 120 requests / sec(don't have the exact number right now).
                // "Util.sleep( 11); // try to avoid pacing violation at TWS", the above lousy "sleep" is in milliseconds, which means it wont allow to send more than 90 requests per second
                // "I have found that even if you honor the 50/sec limit TWS pacing errors will occur.  TWS may queue the messages and at a later time send them to the server.  "
                // "The 50/sec limit applies to the interactions with the server, not TWS.  If the data farm connection is broken and you cancel or initiate new data subscriptions, 
                // "TWS will queue these.  When the data farm connection is restored they are sent generating a pacing violation.  Vagaries in TWS internal operations can also 
                // "cause pacing errors.  You can reduce the occurrence, but I don't think there is a way to absolutely prevent pacing errors assuming you are pushing the limit.  
                // "You should be prepared to reconnect if the three pacing error limit is hit."

                // 2018-12: GetAccountInfoPos(): we query 113 RT prices, even without options underlyings. "Waiting for RT prices: 1297.74 ms. Queried: 113, ReceivedOk: 113, ReceivedErr: 0, Missing: 0"
                // Now, we have a Callback of BrokerWrapperIb clients can throttle  GetAccountInfoPos() RT price queries.

            }


            if (errorCode == 200)
            {
                // sometimes it happens. When IB server is down. 99% of the time it is at the weekend
                // ErrId: 2165, ErrCode: 200, Msg: No security definition has been found for the request
                // ErrId: 2116, ErrCode: 200, Msg: No security definition has been found for the request
                // ErrId: 2144, ErrCode: 200, Msg: No security definition has been found for the request
                // Id: 1611, ErrCode: 200, Msg: The contract description specified for GLD is ambiguous.  // GLD can be USA or London stock or Futures, Options, etc.
                // Warrants, like "EDMC.WAR." give this. However, we don't care about warrants now.
                if (MktDataSubscriptions.TryGetValue(id, out MktDataSubscription mktDataSubscription))
                {
                    errMsg += $". Id {id} is found in MktDataSubscriptions. Ticker: '{mktDataSubscription.Contract.Symbol}', IsAnyPriceArrived: {mktDataSubscription.IsAnyPriceArrived} on GatewayUser {m_gatewayUser}.";
                    Utils.Logger.Info(errMsg);
                    mktDataSubscription.MarketDataError?.Invoke(id, mktDataSubscription, errorCode, errorMsg);
                    // it is expected that for some stocks, there is no market data, but error message. Fine. Prepare for it and don't wait in that case.
                    return;
                }
                if (!GatewaysWatcher.IsApproximatelyMarketTradingTimeForIgnoringIBErrors())
                    return; // skip processing the error further. Don't send it to HealthMonitor.
            }

            if (errorCode == 201)
            {
                // Id: 19, ErrCode: 201, Msg: Order rejected - reason:The contract is not available for short sale
                isAddOrderInfoToErrMsg = true;
            }

            // after subscribing to Market Snapshot data for a ticker, and we call ClientSocket.cancelMktData(p_marketDataId); that is executed properly
            // however IBGateway receives prices for the same ticker, and gives back the Error message here.
            // It occurs after always all CannceMktData(). We should ignore it.
            // BrokerWrapper.error(). Id: 1010, Code: 300, Msg: Can't find EId with tickerId:1010
            if (errorCode == 300)
                return; // skip processing the error further. Don't send it to HealthMonitor.

            // ErrId: 34, ErrCode: 2137, Msg: The closing order quantity is greater than your current position.
            // Treat it only as a warning. It happens when HarryLong has -100 VXX and then UberVXX buys 150 VXX, which results +50 VXX.
            // The warning is correct, however we expect this to happen. So, don't raise error.
            if (errorCode == 2137)
                return; // skip processing the error further. Don't send it to HealthMonitor.

            if (errorCode == 354)
            {
                // real-time price is queried. And Market data was subscribed, but at the weekend, it returns an error. Swallow it at the weekends.
                // Id: 1049, ErrCode: 354, Msg: Requested market data is not subscribed.
                // Id: 1018, ErrCode: 354, Msg: Requested market data is not subscribed.Delayed market data is not available.NOKIA HEX/TOP/ALL.
                if (MktDataSubscriptions.TryGetValue(id, out MktDataSubscription mktDataSubscription))
                {
                    errMsg += $". Id {id} is found in MktDataSubscriptions. Ticker: '{mktDataSubscription.Contract.Symbol}', IsAnyPriceArrived: {mktDataSubscription.IsAnyPriceArrived} on GatewayUser {m_gatewayUser}.";
                    Utils.Logger.Info(errMsg);
                    mktDataSubscription.MarketDataError?.Invoke(id, mktDataSubscription, errorCode, errorMsg);
                    // it is expected that for some stocks, there is no market data, but error message. Fine. Prepare for it and don't wait in that case.
                    return;
                }
                else if (CancelledMktDataSubscriptions.TryGetValue(id, out mktDataSubscription))
                {
                    errMsg += $". Id {id} is found in CancelledMktDataSubscriptions. Ticker: '{mktDataSubscription.Contract.Symbol}', IsAnyPriceArrived: {mktDataSubscription.IsAnyPriceArrived} on GatewayUser {m_gatewayUser}.";

                    if (!mktDataSubscription.IsAnyPriceArrived)
                    {
                        Utils.Logger.Info(errMsg + " Expected error for failed IB reaction. If mktData that was cancelled already AND IsAnyPriceArrived=false, then ignore this error. Don't send HealthMonitor message.");
                        return; // If mktData that was cancelled already AND IsAnyPriceArrived=false, then ignore this error. This happens rarely when IB doesn't give any price for a ticker, then 2 seconds later we Cancel that MktData subscription. Then this error comes. It is expected.
                    }
                }
                else
                {
                    errMsg += $". Id {id} cannot be found in MktDataSubscriptions or CancelledMktDataSubscriptions.";
                }

                if (!GatewaysWatcher.IsApproximatelyMarketTradingTimeForIgnoringIBErrors()) // mktDataSubscription.MarketDataError?.Invoke() is needed, even after market closed
                    return; // skip processing the error further. Don't send it to HealthMonitor.
            }

            if (errorCode == 404)
            {
                // ErrId: 28, ErrCode: 404, Msg: Order held while securities are located.  // when stocks cannot be borrowed for shorting. OrderID = Id
                isAddOrderInfoToErrMsg = true;
            }

            if (errorCode == 506)
            {
                // sometimes it happens at connection. skip this error. IF Connection doesn't happen after trying it 3 times. VBGateway will notify HealthMonitor anyway.
                // Once per month, this error happens, so the first connection fails, but the next connection goes perfectly through.
                // ErrId: 42=the ClientID, ErrCode: 506, Msg: Unsupported version
                // id == 41, or 42, which is the BrokerConnectionClientID
                return; // skip processing the error further. Don't send it to HealthMonitor.
            }

            if (errorCode == 2129)
            {
                //"Id: -1, ErrCode: 2129, Msg: The product INNL.CVR is provided on an indicative and informational basis only. IB does not represent that the valuation for this instrument is accurate. The basis for the calculation may change at any time. Traders are responsible for understanding the contract details and details of deliverable instruments independently of IB sources, which are provided on a best efforts basis only."
                return;
            }

            if (errorCode == 10168) // it happened at the weekend, but IB fixed their mistake.
            {
                //"Id: 1358, ErrCode: 10168, Msg: Requested market data is not subscribed. Delayed market data is not enabled"
                if (!GatewaysWatcher.IsApproximatelyMarketTradingTimeForIgnoringIBErrors()) // mktDataSubscription.MarketDataError?.Invoke() is needed, even after market closed
                    return; // skip processing the error further. Don't send it to HealthMonitor.
            }

            // SERIOUS ERRORS AFTER THIS LINE. Notify HealthMonitor.
            // after asking realtime price as "s=^VIX,^^^VIX201610,^^^VIX201611,^^^VIX201701,^^^VIX201704&f=l"
            // Code: 200, Msg: The contract description specified for VIX is ambiguous; you must specify the multiplier or trading class.
            if (isAddOrderInfoToErrMsg)
            {
                if (!OrderSubscriptions.TryGetValue(id, out OrderSubscription orderSubscription))
                {
                    errMsg += $". OrderId {id} cannot be found in OrderSubscriptions";
                }
                else
                {
                    errMsg += $". OrderId {id} is found in OrderSubscriptions: {orderSubscription.Order.Action} {orderSubscription.Order.TotalQuantity} {orderSubscription.Contract.Symbol} on GatewayUser {m_gatewayUser}.";
                }
            }
            error(errMsg);
        }

        static string GetUsefulAccountSummaryTags()
        {
            // Others can be calculated from these. E.g.
            // AccountSummaryTags.AvailableFunds = NetLiquidation - InitMarginReq
            // Leverage: GrossPositionValue / NetLiquidation
            return AccountSummaryTags.NetLiquidation + "," + AccountSummaryTags.GrossPositionValue + "," + AccountSummaryTags.InitMarginReq + "," + AccountSummaryTags.MaintMarginReq + "," + AccountSummaryTags.TotalCashValue;
        }

        public virtual int ReqAccountSummary()
        {
            int reqId = GetUniqueReqAccountSumID;
            //ClientSocket.reqAccountSummary(reqId, "All", AccountSummaryTags.GetAllTags());      /***Subscribing to an account's information. Only one at a time! ***/
            ClientSocket.reqAccountSummary(reqId, "All", GetUsefulAccountSummaryTags());        /*** Subscribing to an account's information. Only one at a time! ***/

            return reqId;
        }

        public virtual void CancelAccountSummary(int p_reqId)
        {
            ClientSocket.cancelAccountSummary(p_reqId);
        }

        public virtual void ReqPositions()
        {
            ClientSocket.reqPositions();
        }

        // https://www.interactivebrokers.co.uk/en/software/tws/usersguidebook/thetradingwindow/price-based.htm
        // MARK_PRICE (Mark Price (used in TWS P&L computations)): can be calculated. Maybe don't store it.
        //The mark price is equal to the LAST price unless:
        //Ask<Last - the mark price is equal to the ASK price.
        //Bid> Last - the mark price is equal to the BID price.
        //Mid price: can be calculated, don't store it. The midpoint between the current bid and ask.
        //public bool GetPrice(Contract p_contract, int p_tickType, out double p_value)
        //{
        //    p_value = Double.NaN;
        //    return false;
        //}

        public virtual void currentTime(long time)
        {
            Console.WriteLine("Current Time: " + time);
        }

        // 1. IB do not provide tick data. For U.S. Equities, you get one price update (not a tick!) per 250ms. Assuming that the exchanges step at 1ms, TTBOMK, this is some kind of volume-weighted average of these 250 data points.
        public virtual int ReqMktDataStream(Contract p_contract, string p_genericTickList = null, bool p_snapshot = false, MktDataSubscription.MktDataArrivedFunc p_mktDataArrivedFunc = null, MktDataSubscription.MktDataErrorFunc p_mktDataErrorFunc = null, MktDataSubscription.MktDataTickGenericFunc p_mktDataTickGenericFunc = null, MktDataSubscription.MktDataTypeFunc p_mktDataTypeFunc = null, MktDataSubscription.MktDataTickOptionComputationFunc p_mktDataTickOptionComputationFunc = null)
        {
            int marketDataId = GetUniqueReqMktDataID;
            Utils.Logger.Debug($"ReqMktDataStream() {p_contract.Symbol}{((p_contract.LocalSymbol != null)? ("(" + p_contract.LocalSymbol + ")"):"")}: { marketDataId} START");

            var mktDataSubscr = new MktDataSubscription()
            {
                Contract = p_contract,
                MarketDataId = marketDataId,
                IsSnapshot = p_snapshot,
                MarketDataArrived = p_mktDataArrivedFunc,
                MarketDataError = p_mktDataErrorFunc,
                MarketDataTickGeneric = p_mktDataTickGenericFunc,
                MarketDataType = p_mktDataTypeFunc,
                MarketDataTickOptionComputation = p_mktDataTickOptionComputationFunc,
            };
            // RUT index data comes once ever 5 seconds
            if (!p_snapshot)    // only if it is a continous streaming
                mktDataSubscr.CheckDataIsAliveTimer = new System.Threading.Timer(new TimerCallback(MktDataIsAliveTimer_Elapsed), mktDataSubscr, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(-1.0));
            MktDataSubscriptions.TryAdd(marketDataId, mktDataSubscr);

            ClientSocket.reqMarketDataType(2);    // 2: streaming data (for realtime), 1: frozen (for historical prices)
            //ClientSocket.reqMktData(marketDataId, p_contract, "221", false, null);    // p_snapshot = false, stream is needed for IbMarkPrice. Otherwise: Id: 1002, ErrCode: 321, Msg: Error validating request:-'bR' : cause - Snapshot market data subscription is not applicable to generic ticks; 
            ClientSocket.reqMktData(marketDataId, p_contract, p_genericTickList, p_snapshot, null);

            Utils.Logger.Debug($"ReqMktDataStream() {p_contract.Symbol}: { marketDataId} END");
            return marketDataId;
        }

        public virtual void CancelMktData(int p_marketDataId)
        {
            Utils.Logger.Debug($"CancelMktData() { p_marketDataId} START");
            MktDataSubscriptions.TryGetValue(p_marketDataId, out MktDataSubscription mktDataSubscription);

            // 1. at first, inform IBGateway to not send data
            if (!mktDataSubscription.IsSnapshot)
                ClientSocket.cancelMktData(p_marketDataId); // if p_snapshot = true, it is not necessarily to Cancel. However, it doesn't hurt.

            // 2. Only after informing IBGateway delete the record from our memory DB
            CancelledMktDataSubscriptions.TryAdd(p_marketDataId, mktDataSubscription);        // store it for logging purposes. For error message "Requested market data is not subscribed."
            MktDataSubscriptions.TryRemove(p_marketDataId, out mktDataSubscription);
            if (mktDataSubscription.CheckDataIsAliveTimer != null)
                mktDataSubscription.CheckDataIsAliveTimer.Dispose();

            //Utils.Logger.Debug($"CancelMktData() { p_marketDataId} END");
        }

        //- When streaming realtime price of Data for RUT, the very first time of the day, TWS gives price, but IBGateway doesn't give any price. 
        // I have to restart VBroker, so second VBroker run, there is a RUT price.
        //>Solution1: If real time price is not given in 5 minutes, cancel and ask realtime mktData again in the morning
        //	>this would work intraday too.After 20 seconds, we Subscribe to market data.
        //>Solution2: or if previous doesn't work, at least, ask mktData again 1 minutes after market Opened.
        //	>this wouldn't work if VBroker started after market Open, because ReSubscribe wouldn't be called.
        //>Solution3: or maybe do both previous ideas.
        // Good news: Solution1 worked perfectly. After cancelMktData() and reqMktData() again, RUT index data started to come instantly, 
        // but at 8.a.m CET, only last,lastClose,High/Low prices were given (there was no USA market). So, it was really connected the second time.
        // However, when USA market opened, at 14:30, RUT lastPrice data poured in at every 5 seconds.
        public void MktDataIsAliveTimer_Elapsed(object p_state)    // Timer is coming on a ThreadPool thread
        {
            try
            {
                MktDataSubscription mktDataSubscr = (MktDataSubscription)p_state;
                if (mktDataSubscr.IsAnyPriceArrived)  // we had at least 1 price, so everything seems ok.
                    return;

                Console.WriteLine($"MktDataIsAliveTimer_Elapsed(): No price found for {mktDataSubscr.Contract.Symbol}. Cancel and re-subscribe with the same marketDataId.");
                Utils.Logger.Info($"MktDataIsAliveTimer_Elapsed(): No price found for {mktDataSubscr.Contract.Symbol}. Cancel and re-subscribe with the same marketDataId.");
                ClientSocket.cancelMktData(mktDataSubscr.MarketDataId);
                ClientSocket.reqMarketDataType(2);    // 2: streaming data (for realtime), 1: frozen (for historical prices)
                ClientSocket.reqMktData(mktDataSubscr.MarketDataId, mktDataSubscr.Contract, null, false, null);     // use the same MarketDataId, so we don't have to update the MktDataSubscriptions dictionary.
            }
            catch (Exception e)
            {
                Utils.Logger.Error(e, "MktDataIsAliveTimer_Elapsed() exception.");
                throw;
            }
        }

        public virtual bool GetAlreadyStreamedPrice(Contract p_contract, ref Dictionary<int, PriceAndTime> p_quotes)
        {
            var mktDataSubscr = MktDataSubscriptions.Values.FirstOrDefault(r => VBrokerUtils.IsContractEqual(r.Contract, p_contract));
            if (mktDataSubscr == null)
            {
                Utils.Logger.Debug($"Market data for Contract {p_contract.Symbol} was not requested as Stream. Do make that request earlier or ask Snapshot data.");
                return false;
            }

            ConcurrentDictionary<int, PriceAndTime> tickData = mktDataSubscr.Prices;
            lock (tickData) // don't lock for too long
            {
                foreach (var item in p_quotes)
                {
                    if (item.Key == TickType.MID)
                    {
                        if (tickData.TryGetValue(TickType.ASK, out PriceAndTime priceAndTimeAsk))
                        {
                            if (tickData.TryGetValue(TickType.BID, out PriceAndTime priceAndTimeBid))
                            {
                                item.Value.Price = (priceAndTimeAsk.Price + priceAndTimeBid.Price) / 2.0;
                                item.Value.Time = (priceAndTimeAsk.Time > priceAndTimeBid.Time) ? priceAndTimeAsk.Time : priceAndTimeBid.Time;  // use the later, bigger Time  (we know that data is not stale, it is actual)
                            }
                        }
                    }
                    else
                    {
                        PriceAndTime priceAndTime = null;
                        if (tickData.TryGetValue(item.Key, out priceAndTime))
                        {
                            item.Value.Time = priceAndTime.Time;
                            item.Value.Price = priceAndTime.Price;
                        }
                    }

                    if (item.Key == TickType.MID || item.Key == TickType.LAST)  // override the time to timestamp, which comes every 1 second.  if item.Key = LastClose price, we don't need to bother with this
                    {
                        // it happens that ASK, BID doesn't change for 40-60 minutes, when the $price is small. For example USO was ASK=10.11, BID=10.10 on 2017-03-22. In even smaller case 1 cent change could be 1%, which means ASK, BID may not change for the whole day.
                        // However, LastPrice was changing more frequently (every minute), alternating between 10.10 or 10.11. When checking the Staleness of MID price, we should use the LastPrice Time, not the time of ASK or BID, because that may not change.
                        // we can give the benefit of the doubt that if askBid is given, but changed a long time ago, LastPrice is also given => we use the lastPrice time, and assume AskBid is still the same value as it was 30 minutes ago.
                        // However, if lastPrice is not frequent either, it is not good either. 
                        // Most of the time, (if last or ask or bid doesn't change) Timestamp comes exactly every 1 second  (if there is a good connection with IB Gateway). That is the best solution to check whether the data is stale.
                        // However, on 2017-03-23, for MVV it was different. Timestamp come sporadically in the morning, just once every 3 minutes. and at 18:35, the last timestamp come. After that no Timestamp, but Ask,Bid changed every 1 second.
                        //0323T17:28:41.123#18#5#Info: Tick string. Ticker Id:1019, Type: lastTimestamp, Value: 1490290120
                        //0323T17: 32:33.175#18#5#Info: Tick string. Ticker Id:1019, Type: lastTimestamp, Value: 1490290352
                        //0323T17: 32:40.564#18#5#Info: Tick string. Ticker Id:1019, Type: lastTimestamp, Value: 1490290360
                        //0323T18: 35:11.396#18#5#Info: Tick string. Ticker Id:1019, Type: lastTimestamp, Value: 1490294111		// that was the last timestamp
                        //... but later, askbid changes frequently:
                        //0323T18: 35:11.645#18#5#Info: Tick Price. Tick Id:1019, Field: bidPrice, Price: 98.83, CanAutoExecute: 1
                        //0323T18: 35:11.896#18#5#Info: Tick Price. Tick Id:1019, Field: bidPrice, Price: 98.81, CanAutoExecute: 1
                        //Therefore, timestamp alone cannot be used. So, Find the Max(TimeStampTime, AskBid time)
                        if (Int64.TryParse(mktDataSubscr.LastTimestampStr, out Int64 timestamp))
                        {
                            DateTime timestampTime = Utils.UnixTimeStampToDateTimeUtc(timestamp);
                            if (timestampTime > item.Value.Time)    // use the later, bigger Time  (we know that data is not stale, it is actual)
                                item.Value.Time = timestampTime;
                        }
                        else  // if time stamp is invalid, NaN, etc. give a warning, but continue
                        {
                            Utils.Logger.Warn($"Warning. Timestamp {mktDataSubscr.LastTimestampStr} cannot be converted to Int64. We have the Realtime price of {TickType.getField(item.Key)} for '{p_contract.Symbol}', which is {item.Value.Price:F2}. Maybe Gateway was disconnected. Instead of timestamp date, we fall back to the data last update date.");
                        }
                    }
                }
            }

            bool isOk = true;
            foreach (var item in p_quotes)
            {
                if (Double.IsNaN(item.Value.Price))
                    isOk = false;   // expected behaviour. Imagine client asked for ASK, BID, LAST, but we only have LAST. In that case Price=NaN for ASK,BID, but we should return the LAST price

                if (item.Value.Price < 0.0)
                {
                    Utils.Logger.Warn($"Warning. Something is wrong. Price is negative. Returning False for GetAlreadyStreamedPrice().");   // however, VBroker may want to continue, so don't throw Exception or do StrongAssert()
                    isOk = false;
                }
                // for daily High, Daily Low, Previous Close, etc. don't check this staleness
                bool doCheckDataStaleness = !Double.IsNaN(item.Value.Price) &&
                    (item.Key != TickType.LOW && item.Key != TickType.HIGH && item.Key != TickType.CLOSE);
                if (doCheckDataStaleness)
                {
                    DateTime quoteAcquirationTime = item.Value.Time;
                    if ((DateTime.UtcNow - quoteAcquirationTime).TotalMinutes > 35.0)
                    {
                        Utils.Logger.Warn($"Warning. Something may be wrong. We have the Realtime price of {TickType.getField(item.Key)} for '{p_contract.Symbol}' (MarketDataId:{mktDataSubscr.MarketDataId}), which is {item.Value.Price:F2} , but it ({quoteAcquirationTime.ToString()}) is older than 35 minutes. Maybe Gateway was disconnected. Returning False for price.");
                        isOk = false;
                    }
                }
            }

            return isOk;
        }

        // After subscribing by reqMktData...
        //- mainClient.reqMktData(marketDataId, contractSPY, null, false, null);
        //- using "221" messages: mainClient.reqMktData(marketDataId, contractSPY, "221", false, null); results the same
        //- null      // give price+size
        //- 221 	Mark Price(used in TWS P&L computations) 	// give price+size+markPrice (even if there is no LastTrade, MarkPrice can change based on AskPrice, BidPrice)
        //- 225 	Auction values(volume, price and imbalance) // give price+size+AuctionValues
        //- 233 	RTVolume - contains the last trade price, last trade size, last trade time, total volume, VWAP, and single trade flag. // give price+size too
        // Conclusion: cannot get only AskPrice, BidPrice real-time data without the Size. AskSize, BidSize always comes with it. Fine.

        ////1. These are the received ticks after subscription to realtime data: (high/low/previousClose/Open)
        //Tick Price.Ticker Id:1001, Field: bidPrice, Price: 22.01, CanAutoExecute: 1
        //Tick Size. Ticker Id:1001, Field: bidSize, Size: 3

        //Tick Price. Ticker Id:1001, Field: askPrice, Price: 22.02, CanAutoExecute: 1
        //Tick Size. Ticker Id:1001, Field: askSize, Size: 217

        //Tick Price. Ticker Id:1001, Field: lastPrice, Price: 22.01, CanAutoExecute: 0
        //Tick Size. Ticker Id:1001, Field: lastSize, Size: 2

        //Tick Size. Ticker Id:1001, Field: bidSize, Size: 3
        //Tick Size. Ticker Id:1001, Field: askSize, Size: 217
        //Tick Size. Ticker Id:1001, Field: lastSize, Size: 2

        //Tick Size. Ticker Id:1001, Field: volume, Size: 724693
        //Tick Price. Ticker Id:1001, Field: high, Price: 22.08, CanAutoExecute: 0
        //Tick Price. Ticker Id:1001, Field: low, Price: 20.95, CanAutoExecute: 0
        //Tick Price. Ticker Id:1001, Field: close, Price: 21.59, CanAutoExecute: 0
        //Tick Price. Ticker Id:1001, Field: open, Price: 21.31, CanAutoExecute: 0
        //Tick string. Ticker Id:1001, Type: lastTimestamp, Value: 1457126686, which is a UNIX timestamp epoch: https://www.epochconverter.com/  seconds since Jan 01 1970. (UTC)

        ////2. These were the initial values. Later, this is the regular changes that comes 
        //Tick Generic. Ticker Id:1001, Field: halted, Value: 0
        //Tick Size. Ticker Id:1001, Field: askSize, Size: 206
        //Tick Generic. Ticker Id:1001, Field: halted, Value: 0
        //Tick Size. Ticker Id:1001, Field: volume, Size: 724722
        //Tick Size. Ticker Id:1001, Field: askSize, Size: 204
        //Tick Size. Ticker Id:1001, Field: bidSize, Size: 8
        public virtual void tickPrice(int tickId, int field, double price, int canAutoExecute)
        {
            Utils.Logger.Info("Tick Price. Tick Id:" + tickId + ", Field: " + TickType.getField(field) + ", Price: " + price + ", CanAutoExecute: " + canAutoExecute);

            if (!MktDataSubscriptions.TryGetValue(tickId, out MktDataSubscription mktDataSubscription))
            {
                Utils.Logger.Debug($"tickPrice(). MktDataSubscription tickerID { tickId} is not expected. Although IBGateway can send some prices even after CancelMktData was sent to IBGateway.");
                return;
            }

            if (!mktDataSubscription.IsAnyPriceArrived)
            {
                //Console.WriteLine($"Firstprice: {mktDataSubscription.Contract.Symbol}, {TickType.getField(field)}, {price}");  // don't clutter Console
                Utils.Logger.Trace($"Firstprice: {mktDataSubscription.Contract.Symbol}, {TickType.getField(field)}, {price}");
                mktDataSubscription.IsAnyPriceArrived = true;
            }

            //if (mktDataSubscription.Contract.Symbol == "RUT")   // temporary: for debugging purposes
            //{
            //    Console.WriteLine($"RUT: {mktDataSubscription.Contract.Symbol}, {TickType.getField(field)}, {price}");
            //}

            ConcurrentDictionary<int, PriceAndTime> tickData = mktDataSubscription.Prices;
            lock (tickData)
            {
                if (tickData.ContainsKey(field))     // the Decimal is 20x slower than float, we don't use it: http://gregs-blog.com/2007/12/10/dot-net-decimal-type-vs-float-type/
                {
                    tickData[field].Price = price;
                    tickData[field].Time = DateTime.UtcNow;
                }
                //else
                //    Console.WriteLine("Tick Price. Tick Id:" + tickId + ", Field: " + TickType.getField(field) + ", Price: " + price + ", CanAutoExecute: " + canAutoExecute);
            }

            mktDataSubscription.MarketDataArrived?.Invoke(tickId, mktDataSubscription, field, price);
        }


        public virtual void tickSize(int tickerId, int field, int size)
        {
            // we don't need the AskSize, BidSize, LastSize values, so we don't process them unnecessarily.
            //Console.WriteLine("Tick Size. Tick Id:" + tickerId + ", Field: " + TickType.getField(field)  + ", Size: " + size);
        }

        public virtual void tickString(int tickerId, int tickType, string p_value)
        {
            if (tickType == TickType.LAST_TIMESTAMP)
            {
                // lastTimestamp example: "1303329585"
                Utils.Logger.Info("Tick string. Tick Id:" + tickerId + ", Type: " + TickType.getField(tickType) + ", Value: " + p_value);
                MktDataSubscription mktDataSubscription = null;
                if (!MktDataSubscriptions.TryGetValue(tickerId, out mktDataSubscription))
                {
                    Utils.Logger.Debug($"tickString(). MktDataSubscription tickerID { tickerId} is not expected. Although IBGateway can send some prices even after CancelMktData was sent to IBGateway.");
                    return;
                }
                mktDataSubscription.LastTimestampStr = p_value;
            }
            else if (tickType == TickType.ASK_EXCH || tickType == TickType.BID_EXCH || tickType == TickType.LAST_EXCH)
            {
                // !!! It comes every second for every price data. Don't log to file and don't write to console. The log file is 150MB every day.

                //https://www.interactivebrokers.com/en/index.php?f=5061&ns=T&nhf=T
                //Show Quote Exchange
                //A single data request from the API can receive aggregate quotes from multiple exchanges.With API versions 9.72.18 and TWS 9.62 and higher, the tick types 'bidExch'(tick type 32), 'askExch'(tick type 33), 'lastExch'(tick type 84) are used to identify the source of a quote.To preserve bandwidth, the data returned to these tick types consists of a sequence of capital letters rather than a long list of exchange names for every returned exchange name field.To find the full exchange name corresponding to a single letter code returned in tick types 32, 33, or 84, and API function IBApi::EClient::reqSmartComponents is available.
                //The code for "ARCA" may be "P".In that case if "P" is returned to the exchange tick types, that would indicate the quote was provided by ARCA.
                //Tick string.Tick Id: 1010, Type: askExch, Value: QT
                //Tick string.Tick Id: 1003, Type: askExch, Value: CBQWTMJ
            }
            else
            {
                Utils.Logger.Info("Tick string. Tick Id:" + tickerId + ", Type: " + TickType.getField(tickType) + ", Value: " + p_value);
                Console.WriteLine("Tick string. Tick Id:" + tickerId + ", Type: " + TickType.getField(tickType) + ", Value: " + p_value);
            }
        }

        public virtual void tickGeneric(int tickerId, int field, double value)
        {
            //AskBidLastOpenClose,Prices come randomly, but the last items are lastTimeStamp and Generic Halted. We can use this for timing that we don't expect more data. In case we wait BidPrice when it doesn't exist
            //1129T22: 53:03.382#16#5#Info: Tick Price. Tick Id:1011, Field: close, Price: 0.05, CanAutoExecute: 0
            //1129T22: 53:03.382#16#5#Info: Tick string. Tick Id:1011, Type: lastTimestamp, Value: 1543521327
            //1129T22: 53:03.382#16#5#Info: Tick Generic. Tick Id:1011, Field: halted, Value: 0
            Utils.Logger.Info("Tick Generic. Tick Id:" + tickerId + ", Field: " + TickType.getField(field) + ", Value: " + value);
            if (field == TickType.HALTED)
            {
                //https://www.interactivebrokers.co.uk/en/software/api/apiguide/tables/tick_types.htm
                //0 = Not halted
                //1 = General halt(trading halt is imposed for purely regulatory reasons) with / without volatility halt.
                //2 = Volatility only halt (trading halt is imposed by the exchange to protect against extreme volatility).
                if (value > 0.0)
                {
                    Utils.Logger.Warn("Trading is halted. Tick Generic. Tick Id:" + tickerId + ", Field: " + TickType.getField(field) + ", Value: " + value);
                }
            } else
                Console.WriteLine("Tick Generic. Tick Id:" + tickerId + ", Field: " + TickType.getField(field) + ", Value: " + value);

            if (!MktDataSubscriptions.TryGetValue(tickerId, out MktDataSubscription mktDataSubscription))
            {
                Utils.Logger.Debug($"tickPrice(). MktDataSubscription tickerID { tickerId} is not expected. Although IBGateway can send some prices even after CancelMktData was sent to IBGateway.");
                return;
            }
            mktDataSubscription.MarketDataTickGeneric?.Invoke(tickerId, mktDataSubscription, field, value);
        }

        public virtual void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact, double dividendsToLastTradeDate)
        {
            Console.WriteLine("TickEFP. " + tickerId + ", Type: " + tickType + ", BasisPoints: " + basisPoints + ", FormattedBasisPoints: " + formattedBasisPoints + ", ImpliedFuture: " + impliedFuture + ", HoldDays: " + holdDays + ", FutureLastTradeDate: " + futureLastTradeDate + ", DividendImpact: " + dividendImpact + ", DividendsToLastTradeDate: " + dividendsToLastTradeDate);
        }

        public virtual void tickSnapshotEnd(int tickerId)
        {
            // this comes 8 seconds after ReqMktDataStream(), about 7 seconds after the last data: tickGeneric, so it is not useful to time the end-of snapshot.
            //Console.WriteLine("TickSnapshotEnd: " + tickerId);
            Utils.Logger.Info("TickSnapshotEnd: " + tickerId);
        }

        public virtual void nextValidId(int orderId)
        {
            //Console.WriteLine("Next Valid Id: "+orderId);
            NextOrderId = orderId;
        }

        public virtual void deltaNeutralValidation(int reqId, UnderComp underComp)
        {
            Console.WriteLine("DeltaNeutralValidation. " + reqId + ", ConId: " + underComp.ConId + ", Delta: " + underComp.Delta + ", Price: " + underComp.Price);
        }

        public virtual void managedAccounts(string accountsList)
        {
            IbAccountsList = accountsList;
            //Console.WriteLine("Account list: "+accountsList);
        }

        // streamed or snapshot ReqMktData will send this info continously for Option contracts, not for stocks. We don't need it in general for real time prices, but we need it for GetAccountsInfo() to calculate the DeltaAdjustedDeliveryValue
        // reqMktData() has a param 'mktDataOptions', but it is undocumented, so there is no way to ask IB to NOT send this data.
        // UnderlyingPrice is good for stock options, but totally off for VIX options. We may use it for stock options, to speed up GetAccountsInfo()
        // TickOptionComputation() comes 4 times with different fields from 10..13. All the 4 times the IV is different, therefore the Delta calculation is different. My experience is that the last one (field = 13) seems to be the one that is shown in TWS.
        //{"Symbol":"QQQ","SecType":"OPT","Currency":"USD","Pos":"10","AvgCost":"33.78","LastTradeDate":"20200117","Right":"C","Strike":"250","Multiplier":"100","LocalSymbol":"QQQ   200117C00250000","EstPrice":"0.07","EstUnderlyingPrice":"167.79"}
        //TickOptionComputation.TickerId: 1062, field: 10, ImpliedVolatility: 0.155203259626018, Delta: 0.00656475154392036, OptionPrice: 0.0500000007450581, pvDividend: 1.25896178502438, Gamma: 0.000732138477560608, Vega: 0.0369295524834344, Theta: -0.000745103332077535, UnderlyingPrice: 167.789993286133
        //TickOptionComputation.TickerId: 1062, field: 11, ImpliedVolatility: 0.166019683329638, Delta: 0.0105321251750728, OptionPrice: 0.0900000035762787, pvDividend: 1.25896178502438, Gamma: 0.00103740161541857, Vega: 0.0508112077445905, Theta: -0.00120614030071572, UnderlyingPrice: 167.789993286133
        //TickOptionComputation.TickerId: 1062, field: 12, ImpliedVolatility: 0.154653871169709, Delta: 0.00639224036956221, OptionPrice: 0.0500000007450581, pvDividend: 1.25896178502438, Gamma: 0.000717526218798153, Vega: 0.0359134002027889, Theta: -0.000725136237099267, UnderlyingPrice: 167.789993286133
        //TickOptionComputation.TickerId: 1062, field: 13, ImpliedVolatility: 0.158388264566503, Delta: 0.00752481853860245, OptionPrice: 0.0610202906226993, pvDividend: 1.25896178502438, Gamma: 0.00081107874803257, Vega: 0.0427398500810083, Theta: -0.000858885577012274, UnderlyingPrice: 167.779998779297
        //>IB shows 0.008, none of them is that, but I can use the last one(field = 13) ,nd round it, then you round it up, so that is the used value.
        //So, just use the last Delta value.
        public virtual void tickOptionComputation(int tickerId, int field, double impliedVolatility, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice)
        {
            string logStr = "TickOptionComputation. TickerId: " + tickerId + ", field: " + field + ", ImpliedVolatility: " + impliedVolatility + ", Delta: " + delta
                + ", OptionPrice: " + optPrice + ", pvDividend: " + pvDividend + ", Gamma: " + gamma + ", Vega: " + vega + ", Theta: " + theta + ", UnderlyingPrice: " + undPrice;

            Utils.Logger.Trace(logStr);
            Console.WriteLine(logStr);  // TEMP until feature is developed

            if (!MktDataSubscriptions.TryGetValue(tickerId, out MktDataSubscription mktDataSubscription))
            {
                Utils.Logger.Debug($"tickPrice(). MktDataSubscription tickerID { tickerId} is not expected. Although IBGateway can send some prices even after CancelMktData was sent to IBGateway.");
                return;
            }
            mktDataSubscription.MarketDataTickOptionComputation?.Invoke(tickerId, mktDataSubscription, field, impliedVolatility, delta, undPrice);
        }

        public virtual void accountSummary(int reqId, string account, string tag, string value, string currency)
        {
            Utils.Logger.Trace("Acct Summary. ReqId: " + reqId + ", Acct: " + account + ", Tag: " + tag + ", Value: " + value + ", Currency: " + currency);
            m_accSumArrCb?.Invoke(reqId, tag, value, currency);
        }

        public virtual void accountSummaryEnd(int reqId)
        {
            Utils.Logger.Trace("AccountSummaryEnd. Req Id: " + reqId);
            m_accSumEndCb?.Invoke(reqId);
        }

        public virtual void updateAccountValue(string key, string value, string currency, string accountName)
        {
            Console.WriteLine("UpdateAccountValue. Key: " + key + ", Value: " + value + ", Currency: " + currency + ", AccountName: " + accountName);
        }

        public virtual void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealisedPNL, double realisedPNL, string accountName)
        {
            Console.WriteLine("UpdatePortfolio. " + contract.Symbol + ", " + contract.SecType + " @ " + contract.Exchange
                + ": Position: " + position + ", MarketPrice: " + marketPrice + ", MarketValue: " + marketValue + ", AverageCost: " + averageCost
                + ", UnrealisedPNL: " + unrealisedPNL + ", RealisedPNL: " + realisedPNL + ", AccountName: " + accountName);
        }

        public virtual void updateAccountTime(string timestamp)
        {
            Console.WriteLine("UpdateAccountTime. Time: " + timestamp);
        }

        public virtual void accountDownloadEnd(string account)
        {
            Console.WriteLine("Account download finished: " + account);
        }

        public virtual void orderStatus(int p_realOrderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld)
        {
            Utils.Logger.Info("OrderStatus. Id: " + p_realOrderId + ", Status: " + status + ", Filled: " + filled + ", Remaining: " + remaining
                + ", AvgFillPrice: " + avgFillPrice + ", PermId: " + permId + ", ParentId: " + parentId + ", LastFillPrice: " + lastFillPrice + ", ClientId: " + clientId + ", WhyHeld: " + whyHeld);

            if (!OrderSubscriptions.TryGetValue(p_realOrderId, out OrderSubscription orderSubscription))
            {
                Utils.Logger.Error($"OrderSubscription orderId {p_realOrderId} is not expected");
                return;
            }

            OrderStatus orderStatus;
            if (!Enum.TryParse<OrderStatus>(status, true, out orderStatus))
            {
                orderStatus = OrderStatus.Unrecognized;
                Utils.Logger.Error($"Order status string {status} was not recognised to Enum. We still continue.");
            }
            orderSubscription.DateTime = DateTime.UtcNow;
            orderSubscription.OrderStatus = orderStatus;
            orderSubscription.Filled = filled;
            orderSubscription.Remaining = remaining;
            orderSubscription.AvgFillPrice = avgFillPrice;
            orderSubscription.PermId = permId;
            orderSubscription.ParentId = parentId;
            orderSubscription.LastFillPrice = lastFillPrice;
            orderSubscription.ClientId = clientId;
            orderSubscription.WhyHeld = whyHeld;

            if (String.Equals(status, "Filled", StringComparison.CurrentCultureIgnoreCase))
                orderSubscription.AutoResetEvent.Set();  // signal to other thread
        }

        // "Feeds in currently open orders." We can subscribe to all the current OrdersInfo. For MOC orders for example, before PlaceOrder() we should check that if there is already an MOC order then we Modify that (or Cancel&Recreate)
        public virtual void openOrder(int orderId, Contract contract, Order order, OrderState orderState)
        {
            Utils.Logger.Info("OpenOrder. ID: " + orderId + ", " + contract.Symbol + ", " + contract.SecType + " @ " + contract.Exchange + ": " + order.Action + ", " + order.OrderType + " " + order.TotalQuantity + ", " + orderState.Status);
        }

        public virtual void openOrderEnd()
        {
            Utils.Logger.Info("OpenOrderEnd");
        }

        public int PlaceOrder(Contract p_contract, TransactionType p_transactionType, double p_volume, OrderExecution p_orderExecution, OrderTimeInForce p_orderTif, double? p_limitPrice, double? p_stopPrice, double p_estimatedPrice, bool p_isSimulatedTrades)
        {

            Order order = new Order();
            switch (p_transactionType)
            {
                case TransactionType.BuyAsset:
                    order.Action = "BUY";
                    break;
                case TransactionType.SellAsset:
                    order.Action = "SELL";
                    break;
                default:
                    throw new Exception($"Unexpected transactionType: {p_transactionType}");
            }

            order.TotalQuantity = p_volume;
            if (p_limitPrice != null)
                order.LmtPrice = (double)p_limitPrice;

            switch (p_orderExecution)
            {
                case OrderExecution.Market:
                    order.OrderType = "MKT";
                    break;
                case OrderExecution.MarketOnClose:
                    order.OrderType = "MOC";
                    break;
                default:
                    throw new Exception($"Unexpected OrderExecution: {p_orderExecution}");
            }

            switch (p_orderTif)
            {
                case OrderTimeInForce.Day:  // Day is the default, so don't do anything
                    order.Tif = "DAY";
                    break;
                default:
                    throw new Exception($"Unexpected OrderTimeInForce: {p_orderTif}");
            }

            int p_realOrderId = NextOrderId++;
            OrderSubscriptions.TryAdd(p_realOrderId, new OrderSubscription() { Contract = p_contract, Order = order });
            if (!p_isSimulatedTrades)
                ClientSocket.placeOrder(p_realOrderId, p_contract, order);
            return p_realOrderId;
        }

        // + note that even MKT (not LMT) orders can hand out and fail due to Short restrictions ("not available for short" or "The SEC Rule 201 (aka "Up-tick Rule) has been triggered", shorting is possible only on Upticks.)
        // see "MOC order execution and short or long decisions (IB).txt"
        // + at the moment, if VBroker fails and time-outs on the trade, we have to do the trade manually quickly, or let VBroker do it next day automatically.
        // Shorting problem: "Order held while securities are located.": (if it happens too many times, consider to change traded instrument e.g. from UVXY to TVIX)
        // Also UVXY borrowing fee rate: 12.16%, while TVIX is 3.73%. That is 8% difference per year, althought with 35% Harry Long weight, it is only about 3% CAGR.
        // in 2017-01: UVXY is 28, while TVIX is 6. After TVIX will have a reverse split, consider trading TVIX instead of UVXY, if these shorting problems occur
        // failed shorting happened:
        // + 2017-01-23: UVXY : "not located" (while at the same time TVIX was available for short)
        // + 2017-02-08 and 09 and 10(Fri), 13(Mon): UVXY : "not located" (while at the same time TVIX was available for short)
        // + 2017-02-14(Tue): UVXY : "not located" (while at the same time TVIX was available for short)
        // "M38482892	2017/02/14 16:33:39 (this is 33 minutes after market closed)	SHORT STOCK POSITION BOUGHT IN	This alert is to inform you that due to a recall IB is unable meet your 
        // settlement delivery obligations for the short stock position(s) listed below for account U****941. As current SEC regulations require that all transactions be settled 
        // on the standard settlement date, these short stock positions have been bought-in. While IB tries to give advanced notice of a possible buy-in, due to the time frame of this fail, 
        // in this instance we were unable to do so. The positions listed below have been bought-in: UVXY (84 shares)
        // This shows the danger of waiting too much. If the "Shortable" column in IB is off for 4 days then on day 5, I had this problem. And IB DIDN'T even warn about it.
        // it was simply bought in 20 minutes after market closed, and they sent the Message in the email 33 minutes after market close. In the old times, at least they warned me on the previous day.
        // I expected that warning email. I wanted to switch to TVIX after that warning email. However, we cannot rely on that warning email. Next time, it is not shortable for 3 days, assume the worst.
        // My UVXY position was $1500.  The ClosePrice was 18.75. However, 20 minutes after market closed, price was 18.50, even better. 40 minutes after market close, when I noticed it, price was even better, 18.32.
        // But their buying price was 19.02. Considering the 18.75 MOC price, it is about 1.4% loss, which is $21. However, who knows, maybe UVXY tomorrow mean reverts. So it was good.
        // Funny news: next day at MOC price was 19.81. So IB buying it for 19.02 was better than if I change UVXY to TVIX 1 day later. 
        // It was a 4.3% better price for me, so actually I profited $62 on the fact that IB made this forced buy-in without warning me before.
        // + 2017-02-14: Decision was made to switch from UVXY to TVIX, as UVXY cannot be shorted. If TVIX is difficult to short, I may short 2x VXX, or long 2x XIV or long 2x SXVY.
        // on the top of it Borrowing fees: UVXY: it was 12%, but now 20%. TVIX: 3.5%.


        public bool WaitOrder(int p_realOrderId, bool p_isSimulatedTrades)
        {
            // wait here
            if (!OrderSubscriptions.TryGetValue(p_realOrderId, out OrderSubscription orderSubscription))
            {
                Utils.Logger.Error($"OrderSubscription orderId {p_realOrderId} is not expected");
                return false;
            }

            string orderType = orderSubscription.Order.OrderType;

            if (p_isSimulatedTrades)    // for simulated orders, pretend its is executed already, even for MOC orders, because the BrokerTask that Simulates intraday doesn't want to wait until it is finished at MarketClose
                return true;

            if (orderType == "MKT")
            {
                bool signalReceived = orderSubscription.AutoResetEvent.WaitOne(TimeSpan.FromMinutes(2)); // timeout of 2 minutes. Don't wait forever, because that will consume this thread forever
                if (!signalReceived)
                    return false;   // if it was a timeout
            } else if (orderType == "MOC")
            {
                // calculate times until MarketClose and wait max 2 minutes after that
                bool isMarketTradingDay;
                DateTime marketOpenTimeUtc, marketCloseTimeUtc;
                bool isTradingHoursOK = Utils.DetermineUsaMarketTradingHours(DateTime.UtcNow, out isMarketTradingDay, out marketOpenTimeUtc, out marketCloseTimeUtc, TimeSpan.FromDays(3));
                if (!isTradingHoursOK)
                {
                    Utils.Logger.Error("WaitOrder().DetermineUsaMarketTradingHours() was not ok.");
                    return false;
                }
                if (!isMarketTradingDay)
                {
                    Utils.Logger.Error("WaitOrder().isMarketTradingDay is false. That is impossible. Order shouldn't have been placed.");
                    return false;
                }
                DateTime marketClosePlusExtra = marketCloseTimeUtc.AddMinutes(2);
                Utils.Logger.Info($"WaitOrder() waits until {marketClosePlusExtra.ToString("HH:mm:ss")}");
                TimeSpan timeToWait = marketClosePlusExtra - DateTime.UtcNow;
                if (timeToWait < TimeSpan.Zero)
                    return true;

                bool signalReceived = orderSubscription.AutoResetEvent.WaitOne(timeToWait); // timeout of 2 minutes. Don't wait forever, because that will consume this thread forever
                if (!signalReceived)
                    return false;   // if it was a timeout
            }

            return true;
        }

        public bool GetRealOrderExecutionInfo(int p_realOrderId, ref OrderStatus p_realOrderStatus, ref double p_realExecutedVolume, ref double p_realExecutedAvgPrice, ref DateTime p_execptionTime, bool p_isSimulatedTrades)
        {
            OrderSubscription orderSubscription = null;
            if (!OrderSubscriptions.TryGetValue(p_realOrderId, out orderSubscription))
            {
                Utils.Logger.Error($"OrderSubscription orderId {p_realOrderId} is not expected");
                return false;
            }

            if (p_isSimulatedTrades)    // there was no orderStatus(), so just fake one
            {
                p_realOrderStatus = OrderStatus.Filled;
                p_realExecutedVolume = orderSubscription.Order.TotalQuantity;    // maybe less is filled than it was required...
                p_realExecutedAvgPrice = 1.0;   // assume we bought it for $1.0 each // we can do RealTime price or YahooEstimated price, or lastDay Closeprice later if it is required
                p_execptionTime = DateTime.UtcNow;
                return true;
            }

            p_realOrderStatus = orderSubscription.OrderStatus;
            p_realExecutedVolume = orderSubscription.Filled;    // maybe less is filled than it was required...
            p_realExecutedAvgPrice = orderSubscription.AvgFillPrice;
            p_execptionTime = orderSubscription.DateTime;
            return true;
        }


        public virtual void contractDetails(int reqId, ContractDetails contractDetails)
        {
            Console.WriteLine("ContractDetails. ReqId: " + reqId + " - " + contractDetails.Summary.Symbol + ", " + contractDetails.Summary.SecType + ", ConId: " + contractDetails.Summary.ConId + " @ " + contractDetails.Summary.Exchange);
        }

        public virtual void contractDetailsEnd(int reqId)
        {
            Console.WriteLine("ContractDetailsEnd. " + reqId);
        }

        public virtual void execDetails(int reqId, Contract contract, Execution execution)
        {
            //Console.WriteLine("ExecutionDetails. " + reqId + " - " + contract.Symbol + ", " + contract.SecType + ", " + contract.Currency + " - " + execution.ExecId + ", " + execution.OrderId + ", " + execution.Shares);
            Utils.Logger.Info("ExecutionDetails. ReqId:" + reqId + " - " + contract.Symbol + ", " + contract.SecType + ", " + contract.Currency + " ,executionId: " + execution.ExecId + ", orderID:" + execution.OrderId + ", nShares:" + execution.Shares);
        }

        public virtual void execDetailsEnd(int reqId)
        {
            Console.WriteLine("ExecDetailsEnd. " + reqId);
        }

        public virtual void commissionReport(CommissionReport commissionReport)
        {
            //Console.WriteLine("CommissionReport. " + commissionReport.ExecId + " - " + commissionReport.Commission + " " + commissionReport.Currency + " RPNL " + commissionReport.RealizedPNL);
            Utils.Logger.Info("CommissionReport. " + commissionReport.ExecId + " - " + commissionReport.Commission + " " + commissionReport.Currency + " RPNL " + commissionReport.RealizedPNL);
        }

        public virtual void fundamentalData(int reqId, string data)
        {
            Console.WriteLine("FundamentalData. " + reqId + "" + data);
        }

        public virtual void marketDataType(int reqId, int marketDataType)
        {
            // Maybe this interpretation is wrong. "marketDataType 1 for real time, 2 for frozen"
            // !! Correct interpretation marketDataType(2)=streaming data (realtime), marketDataType(1)=historical (non-streaming)
            // if we ask m_mainGateway.BrokerWrapper.ReqMktDataStream(new Contract() { Symbol = "RUT", SecType = "IND", Currency = "USD", Exchange = "RUSSELL" });,
            // then After market Close, there is no more realtime price, and this call back tells us that it has a marketDataType=2, which is an Index
            // TMF, VXX can be Frozen(2) too after market close, or at weekend. It means there is no more price data. So, we should signal to clients that don't expect more data. Don't wait.
            Utils.Logger.Info("MarketDataType. " + reqId + ", <!this explanation maybe wrong> Type(1 for real time, 2 for frozen (Index after MarketClose)): " + marketDataType);
            if (!MktDataSubscriptions.TryGetValue(reqId, out MktDataSubscription mktDataSubscription))
            {
                Utils.Logger.Debug($"tickPrice(). MktDataSubscription tickerID { reqId} is not expected. Although IBGateway can send some prices even after CancelMktData was sent to IBGateway.");
                return;
            }
            mktDataSubscription.MarketDataType?.Invoke(reqId, mktDataSubscription, marketDataType);
            mktDataSubscription.PreviousMktDataType = marketDataType;
        }

        public virtual void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size)
        {
            Console.WriteLine("UpdateMarketDepth. " + tickerId + " - Position: " + position + ", Operation: " + operation + ", Side: " + side + ", Price: " + price + ", Size" + size);
        }

        public virtual void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size)
        {
            Console.WriteLine("UpdateMarketDepthL2. " + tickerId + " - Position: " + position + ", Operation: " + operation + ", Side: " + side + ", Price: " + price + ", Size" + size);
        }


        public virtual void updateNewsBulletin(int msgId, int msgType, String message, String origExchange)
        {
            //Console.WriteLine("News Bulletins. " + msgId + " - Type: " + msgType + ", Message: " + message + ", Exchange of Origin: " + origExchange);
            Utils.Logger.Info("News Bulletins. " + msgId + " - Type: " + msgType + ", Message: " + message + ", Exchange of Origin: " + origExchange);
        }

        public virtual void position(string account, Contract contract, double pos, double avgCost)
        {
            Utils.Logger.Trace("Position. " + account + " - Symbol: " + contract.Symbol + ", SecType: " + contract.SecType + ", Currency: " + contract.Currency + ", Exchange: " + contract.Exchange + ", ConId: " + contract.ConId + ", Position: " + pos + ", Avg cost: " + avgCost);
            if (contract.SecType == "OPT" || contract.SecType == "WAR")
                Utils.Logger.Trace($"  Option or Warrant. LastTradeDate: {contract.LastTradeDateOrContractMonth}, Right: {contract.Right}, Strike: {contract.Strike}, Multiplier: {contract.Multiplier}, LocalSymbol:'{contract.LocalSymbol}'");
            m_accPosArrCb?.Invoke(account, contract, pos, avgCost);
        }

        public virtual void positionEnd()
        {
            Utils.Logger.Trace("PositionEnd \n");
            m_accPosEndCb?.Invoke();
        }

        public virtual void realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double WAP, int count)
        {
            Console.WriteLine("RealTimeBars. " + reqId + " - Time: " + time + ", Open: " + open + ", High: " + high + ", Low: " + low + ", Close: " + close + ", Volume: " + volume + ", Count: " + count + ", WAP: " + WAP);
        }

        public virtual void scannerParameters(string xml)
        {
            Console.WriteLine("ScannerParameters. " + xml);
        }

        public virtual void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr)
        {
            Console.WriteLine("ScannerData. " + reqId + " - Rank: " + rank + ", Symbol: " + contractDetails.Summary.Symbol + ", SecType: " + contractDetails.Summary.SecType + ", Currency: " + contractDetails.Summary.Currency
                + ", Distance: " + distance + ", Benchmark: " + benchmark + ", Projection: " + projection + ", Legs String: " + legsStr);
        }

        public virtual void scannerDataEnd(int reqId)
        {
            Console.WriteLine("ScannerDataEnd. " + reqId);
        }

        public virtual void receiveFA(int faDataType, string faXmlData)
        {
            Console.WriteLine("Receing FA: " + faDataType + " - " + faXmlData);
        }

        public virtual void bondContractDetails(int requestId, ContractDetails contractDetails)
        {
            Console.WriteLine("Bond. Symbol " + contractDetails.Summary.Symbol + ", " + contractDetails.Summary);
        }


        // Restrictions:
        // https://www.interactivebrokers.co.uk/en/software/api/apiguide/tables/historical_data_limitations.htm
        // 1. IB Error. Id: 4001, Code: 321, Msg: Error validating request:-'yd' : cause - Historical data request for greater than 365 days rejected.
        // http://www.elitetrader.com/et/index.php?threads/interactive-brokers-maximum-60-historical-data-requests-in-10-minutes.275746/
        // 2. interactive brokers maximum 60 historical data requests in 10 minutes
        // http://www.elitetrader.com/et/index.php?threads/interactive-broker-historical-prices-dividend-adjustment.280815/
        // 3. it is split adjusted (I have checked with FRO for 1:5 split on 2016-02-03), but if dividend is less than 10%, it is not adjusted.
        //"for stock split (and dividend shares of more than 10%), the stock price will be adjusted by the "PAR" value denominator, market cap is the same (shares floating x price) but the price and number of shares outstanding will be adjusted."
        // the returned p_quotes in the last value contains the last realTime price. It comes as CLOSE price for today, but during intraday, this is the realTime lastprice.
        // 4. https://www.interactivebrokers.co.uk/en/software/api/apiguide/tables/historical_data_limitations.htm
        // The following table lists the valid whatToShow values based on the corresponding products. for Index, only TRADES is allowed
        // 5. One of the most important problem: when it is used from one IBGateway on Linux/Windows, later it doesn't work from the other server. Usually works for Stocks, 
        // but for RUT Index, I had a hard time. very unreliable. It is better to get historical data from YF or from our DB. (later I need HistData from many stocks or more than 1 year)
        // 6. read the IBGatewayHistoricalData.txt, but as an essence:
        //maybe, because it is Friday, midnight, that is why RUT historical in unreliable, but the conclusion is:
        //You can use IB historical data: 	>for stocks 	>popular indices, like SPX, but not the RUT.
        //>So, for RUT, implement getting historical from our SQL DB.
        // 7. checked that: the today (last day) of IB.ReqHistoricalData() is not always correct. And it is not always the last real time price. It only works 90% of the time.
        //      2016-07-05: after a 3 days weekend: "Historical data end - 1001 from 20160105  13:50:01 to 20160705  13:50:01 ", and that time (before Market open), realtime price was 13.39.
        //         later on that day, it always give 13.39 for today's last price in ClientSocket.reqHistoricalData. So, don't trust the last day. Asks for a real time price separately from stream.
        //      2016-09-06: the same happend after the 2 days weekend of Labour day. "Historical data end: Date: 20160906, Close: 34.8"
        public virtual bool ReqHistoricalData(DateTime p_endDateTime, int p_lookbackWindowSize, string p_whatToShow, Contract p_contract, out List<QuoteData> p_quotes)
        {
            p_quotes = null;
            int histDataId = GetUniqueReqHistoricalDataID;

            //Console.WriteLine($"ReqHistoricalData() for {p_contract.Symbol}, reqId: {histDataId}");
            Utils.Logger.Info($"ReqHistoricalData() for {p_contract.Symbol}, reqId: {histDataId}");

            // durationString = "60 D" is fine, but "61 D" gives the following error "Historical Market Data Service error message:Time length exceed max.", so after 60, change to Months "3 M" or "11 M"
            string durationString = (p_lookbackWindowSize <= 60) ? $"{p_lookbackWindowSize} D" : $"{p_lookbackWindowSize / 20 + 1} M"; // dividing by int rounds it down. But we want to round it up, so add 1.
            var histDataSubsc = new HistDataSubscription() { Contract = p_contract, QuoteData = new List<QuoteData>(p_lookbackWindowSize) };
            HistDataSubscriptions.TryAdd(histDataId, histDataSubsc);

            //durationString = "5 D";

            ClientSocket.reqHistoricalData(histDataId, p_contract, p_endDateTime.ToString("yyyyMMdd HH:mm:ss"), durationString, "1 day", p_whatToShow, 1, 1, null);    // with daily data formatDate is always "yyyyMMdd", no seconds, and param=2 doesn't give seconds

            // wait here with timeout of 14seconds. In general it is only 1 second, but it took 13sec when  HMDS data farm was disconnected
            // 1109T14:50:02.192#91#5#Info: ReqHistoricalData() for SVXY, reqId: 1001
            // 1109T14:50:06.972#23#5#Debug: BrokerWrapper.error(). ErrId: -1, ErrCode: 2106, Msg: HMDS data farm connection is OK:ushmds
            // 1109T14:50:14.875#23#5#Trace: HistoricalData. 1001 - Date: 20160511, Open: 59.48, High: 61.88, Low: 58.56, Close: 61.4, Volume: 161447, Count: 167906, WAP: 60.284, HasGaps: False
            // 1109T14:50:14.875#23#5#Error: HistDataSubscriptions reqId 1001 is not expected
            bool signalReceived = histDataSubsc.AutoResetEvent.WaitOne(TimeSpan.FromSeconds(14)); // timeout of 14 seconds

            // clean up resources after data arrived
            ClientSocket.cancelHistoricalData(histDataId);

            HistDataSubscription histDataToRemove = null;
            HistDataSubscriptions.TryRemove(histDataId, out histDataToRemove);
            histDataSubsc.AutoResetEvent.Dispose();     // ! AutoResetEvent has a Dispose

            if (!signalReceived)
            {
                Utils.Logger.Error($"ReqHistoricalData() timeout for {p_contract.Symbol}");
                return false;   // if it was a timeout. The Caller may get historical data from SQL DB later.
            }

            if (histDataSubsc.QuoteData.Count > p_lookbackWindowSize)   // if we got too much data, remove the old ones. Very likely it only do shallow copy of values, but no extra memory allocation is required
                histDataSubsc.QuoteData.RemoveRange(0, histDataSubsc.QuoteData.Count - p_lookbackWindowSize);

            p_quotes = histDataSubsc.QuoteData;
            return true;
        }

        public virtual void historicalData(int reqId, string date, double open, double high, double low, double close, int volume, int count, double WAP, bool hasGaps)
        {
            //Console.WriteLine("HistoricalData. " + reqId + " - Date: " + date + ", Open: " + open + ", High: " + high + ", Low: " + low + ", Close: " + close + ", Volume: " + volume + ", Count: " + count + ", WAP: " + WAP + ", HasGaps: " + hasGaps);
            Utils.Logger.Trace("HistoricalData. " + reqId + " - Date: " + date + ", Open: " + open + ", High: " + high + ", Low: " + low + ", Close: " + close + ", Volume: " + volume + ", Count: " + count + ", WAP: " + WAP + ", HasGaps: " + hasGaps);

            HistDataSubscription histDataSubscription = null;
            if (!HistDataSubscriptions.TryGetValue(reqId, out histDataSubscription))
            {
                Utils.Logger.Error($"HistDataSubscriptions reqId { reqId} is not expected");
                return;
            }
            histDataSubscription.QuoteData.Add(new QuoteData() { Date = DateTime.ParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture), AdjClosePrice = close });
        }

        public virtual void historicalDataEnd(int reqId, string startDate, string endDate)
        {
            //Console.WriteLine("Historical data end - " + reqId + " from " + startDate + " to " + endDate);
            Utils.Logger.Trace("Historical data end - " + reqId + " from " + startDate + " to " + endDate);

            HistDataSubscription histDataSubscription = null;
            if (!HistDataSubscriptions.TryGetValue(reqId, out histDataSubscription))
            {
                Utils.Logger.Error($"HistDataSubscriptions reqId { reqId} is not expected");
                return;
            }
            histDataSubscription.AutoResetEvent.Set();  // signal to other thread
        }


        public virtual void verifyMessageAPI(string apiData)
        {
            Console.WriteLine("verifyMessageAPI: " + apiData);
        }
        public virtual void verifyCompleted(bool isSuccessful, string errorText)
        {
            Console.WriteLine("verifyCompleted. IsSuccessfule: " + isSuccessful + " - Error: " + errorText);
        }
        public virtual void verifyAndAuthMessageAPI(string apiData, string xyzChallenge)
        {
            Console.WriteLine("verifyAndAuthMessageAPI: " + apiData + " " + xyzChallenge);
        }
        public virtual void verifyAndAuthCompleted(bool isSuccessful, string errorText)
        {
            Console.WriteLine("verifyAndAuthCompleted. IsSuccessful: " + isSuccessful + " - Error: " + errorText);
        }
        public virtual void displayGroupList(int reqId, string groups)
        {
            Console.WriteLine("DisplayGroupList. Request: " + reqId + ", Groups" + groups);
        }
        public virtual void displayGroupUpdated(int reqId, string contractInfo)
        {
            Console.WriteLine("displayGroupUpdated. Request: " + reqId + ", ContractInfo: " + contractInfo);
        }
        public virtual void positionMulti(int reqId, string account, string modelCode, Contract contract, double pos, double avgCost)
        {
            Console.WriteLine("Position Multi. Request: " + reqId + ", Account: " + account + ", ModelCode: " + modelCode + ", Symbol: " + contract.Symbol + ", SecType: " + contract.SecType + ", Currency: " + contract.Currency + ", Position: " + pos + ", Avg cost: " + avgCost + "\n");
        }
        public virtual void positionMultiEnd(int reqId)
        {
            Console.WriteLine("Position Multi End. Request: " + reqId + "\n");
        }
        public virtual void accountUpdateMulti(int reqId, string account, string modelCode, string key, string value, string currency)
        {
            Console.WriteLine("Account Update Multi. Request: " + reqId + ", Account: " + account + ", ModelCode: " + modelCode + ", Key: " + key + ", Value: " + value + ", Currency: " + currency + "\n");
        }
        public virtual void accountUpdateMultiEnd(int reqId)
        {
            Console.WriteLine("Account Update Multi End. Request: " + reqId + "\n");
        }

        public void connectAck()
        {
            //Console.WriteLine($"connectAck()");
            if (ClientSocket.AsyncEConnect)
                ClientSocket.startApi();
        }

    }
}
