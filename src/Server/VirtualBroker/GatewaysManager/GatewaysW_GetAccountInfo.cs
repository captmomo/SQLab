using DbCommon;
using IBApi;
using SqCommon;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Utils = SqCommon.Utils;

namespace VirtualBroker
{
    public class AccInfo
    {
        public string BrAccStr { get; set; } = String.Empty;
        public Gateway Gateway { get; set; }
        public List<AccSum> AccSums = new List<AccSum>();   // AccSummary
        public List<AccPos> AccPoss = new List<AccPos>();   // Positions
    }

    public class AccSum
    {
        public string Tag { get; set; }
        public string Value { get; set; }
        public string Currency { get; set; }
    }

    public class AccPos
    {
        public Contract Contract { get; set; }
        public int FakeContractID { get; set; } // when we cannot change Contract.ConID which should be left 0, but we use an Int in the dictionary.
        public double Position { get; set; }    // in theory, position is Int (whole number) for all the examples I seen. However, IB gives back as double, just in case of a complex contract. Be prepared.
        public double AvgCost { get; set; }
        public double EstPrice { get; set; } = Double.NaN;  // MktValue can be calculated
        public double EstUndPrice { get; set; }   // In case of options DeliveryValue can be calculated

        public bool IsHidingFromClient { get; set; } = false;
        public int MktDataID { get; set; } = -1;    // for reqMktData
        public double AskPrice { get; set; } = Double.NaN;
        public double BidPrice { get; set; } = Double.NaN;
        public double LastPrice { get; set; } = Double.NaN;
        public double IbMarkPrice { get; set; } = Double.NaN;       // streamed (non-snapshot) mode. Usually LastPrice, but if Last is not in Ask-Bid range, then Ask or Bid, whichever makes sense

        public KeyValuePair<int, List<AccPos>> UnderlyingDictItem { get; set; }

        public double IbComputedImpVol { get; set; } = Double.NaN;
        public double IbComputedDelta { get; set; } = Double.NaN;
        public double IbComputedUndPrice { get; set; } = Double.NaN;
    }


    public partial class GatewaysWatcher
    {

        //$"?v=1&secTok={securityTokenVer2}&bAcc=Gyantal,Charmat,DeBlanzac&data=AccSum,Pos,EstPr,OptDelta&posExclSymbols=VIX,BLKCF,AXXDF&addPrInfoSymbols=QQQ,SPY,TLT,VXX,UNG";
        public string GetAccountsInfo(string p_input)     
        {
            Utils.Logger.Info($"GetAccountsInfo() START with parameter '{p_input}'");
            if (m_mainGateway == null || !m_mainGateway.IsConnected)
            {
                Utils.Logger.Error($"GetAccountsInfo() error. Without mainGateway price data is not possible.");
                return null;
            }
            // Problem is: GetPosition only gives back the Position: 218, Avg cost: $51.16, but that is not enough, because we would like to see the MktValue, DelivValue. 
            // So we need the LastPrice too. Even for options. And it is better to get it in here, than having a separate function call later.
            // 1. Let's collect all the AccountSum for all the ibGateways in a separate threads. This takes about 280msec
            // 2. Let's collect all the AccountPos positions for all the ibGateways in a separate threads. This takes about 28msec to 60msec. Much faster than AccountSum.
            // AccountPos (50msec) should NOT wait for finishing the AccountSum (300msec), but continue quickly processing and getting realtime prices if needed.
            // 3. If client wants RT MktValue too, collect needed RT prices (stocks, options, underlying of options, futures). Use only the mainGateway to ask a realtime quote estimate. So, one stock is not queried an all gateways. Even for options
            // 4. Fill LastPrice, LastUnderlyingPrice in all AccPos for all ibGateways. (Alternatively Calculate the MktValue, DelivValue, but better to just pass the raw data to client)

            var queryDict = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(p_input);     // in .NET core, this is the standard for manipulating queries
            string verQ, secTokQ, bAccQ, dataQ, exclSymbolsQ, addPrInfoSymbolsQ;
            try
            {
                verQ = queryDict["v"];      // it is case sensitive and will throw Exception if not found, but it is fine, it is quick and sure
                secTokQ = queryDict["secTok"];
                bAccQ = queryDict["bAcc"];
                dataQ = queryDict["data"];
                exclSymbolsQ = queryDict["posExclSymbols"];
                addPrInfoSymbolsQ = queryDict["addPrInfoSymbols"];
            }
            catch (Exception e)
            {
                Utils.Logger.Error($"GetAccountsInfo() error. Wrong parameters '{p_input}'. Err: {e.Message}");
                return null;
            }

            if (verQ != "1")    // currently only Version 1.0 is supported
                return null;
            
            char[] charArray = secTokQ.ToCharArray();     // reverse it, so it is not that obvious that it is the seconds
            Array.Reverse(charArray);
            string securityTokenVer2 = new string(charArray);
            if (!Int64.TryParse(securityTokenVer2, out long totalSeconds))
                return null;
            DateTime secTokenTimeBegin = new DateTime(2010, 1, 1);      // we need a security token checking, so 3rd party cannot easily get this data
            DateTime clientQueryTime = secTokenTimeBegin.AddSeconds(totalSeconds);
            var timeDiff = DateTime.UtcNow - clientQueryTime;
            if (timeDiff.TotalHours > 1.0 || timeDiff.TotalHours < -1.0)        // allow 1 hour difference, so a token can be used for a long time
                return null;

            string[] dataArr = dataQ.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            bool isNeedAccSum = dataArr.Contains("AccSum");
            bool isNeedPos = dataArr.Contains("Pos");
            bool isNeedEstPr = dataArr.Contains("EstPr");   // Last price, last underlying price is needed for marketValue
            bool isNeedOptDelta = dataArr.Contains("OptDelta");

            string[] exclSymbolsArr = exclSymbolsQ.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            string[] addPrInfoSymbolsArr = addPrInfoSymbolsQ.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
     
            var allAccInfos = new List<AccInfo>();
            string[] bAccArr = bAccQ.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var bAcc in bAccArr)
            {
                AccInfo accSumPos = new AccInfo { BrAccStr = bAcc };
                switch (bAcc.ToUpper())
                {
                    case "GYANTAL":
                        FindGatewayAndAdd(new GatewayUser[] { GatewayUser.GyantalMain, GatewayUser.GyantalSecondary }, accSumPos);
                        break;
                    case "CHARMAT":
                        FindGatewayAndAdd(new GatewayUser[] { GatewayUser.CharmatMain, GatewayUser.CharmatSecondary }, accSumPos);
                        break;
                    case "DEBLANZAC":
                        FindGatewayAndAdd(new GatewayUser[] { GatewayUser.DeBlanzacMain }, accSumPos);
                        break;
                    default:
                        Utils.Logger.Error($"GetAccountsInfo() error. Unrecognized brokeraccount '{bAcc.ToUpper()}'");
                        continue;
                }

                allAccInfos.Add(accSumPos);
            }

            Task taskAccSum = Task.Run(() =>
            {
                try
                {
                    Stopwatch sw1 = Stopwatch.StartNew();
                    Parallel.ForEach(allAccInfos, accInfo =>        // execute in parallel, so it is faster if DcMain and DeBlanzac are both queried at the same time.
                    {
                        if (accInfo.Gateway == null)
                            return;
                        accInfo.Gateway.GetAccountSums(accInfo.AccSums);        // takes 300msec each in local development, 180msec on ManualTradingServer
                    });
                    sw1.Stop();
                    Console.WriteLine($"GetAccountsInfo()-AllUser_AccSummary ends in {sw1.ElapsedMilliseconds}ms, Thread Id= {Thread.CurrentThread.ManagedThreadId}");
                }
                catch (Exception e)
                {
                    Utils.Logger.Error("GetAccountsInfo()-AllUser_AccSummary ended with exception: " + e.Message);
                }
            });

            Task taskAccPos = Task.Run(() =>
            {
                try
                {
                    Stopwatch sw2 = Stopwatch.StartNew();
                    Parallel.ForEach(allAccInfos, accInfo =>        // execute in parallel, so it is faster if DcMain and DeBlanzac are both queried at the same time.
                    {
                        if (accInfo.Gateway == null)
                            return;
                        accInfo.Gateway.GetAccountPoss(accInfo.AccPoss, exclSymbolsArr);    // takes 50msec each in local development, 90-100msec on 1-CPU (no parallelism, so not relevant) on AutoTradingServer, 3-9msec on 2-CPU fast ManualTradingServer 
                    });
                    //If client wants RT MktValue too, collect needed RT prices (stocks, options, underlying of options, futures). Use only the mainGateway to ask a realtime quote estimate. So, one stock is not queried an all gateways. Even for options
                    if (isNeedEstPr)
                    {
                        
                        CollectEstimatedPrices(allAccInfos, isNeedOptDelta, addPrInfoSymbolsArr);
                    }

                    sw2.Stop();
                    Console.WriteLine($"GetAccountsInfo()-AllUser_AccPos_WithEstPrices ends with CancelMktData() in {sw2.ElapsedMilliseconds}ms, Thread Id= {Thread.CurrentThread.ManagedThreadId}");
                }
                catch (Exception e)
                {
                    Utils.Logger.Error("GetAccountsInfo()-AllUser_AccPos_WithEstPrices ended with exception: " + e.Message);
                }
            });


            Stopwatch sw = Stopwatch.StartNew();
            Task.WaitAll(taskAccSum, taskAccPos);     // AccountSummary() task takes 280msec in local development. (ReqAccountSummary(): 280msec, ReqPositions(): 50msec)
            sw.Stop();
            Console.WriteLine($"GetAccountsInfo() ends in {sw.ElapsedMilliseconds}ms, Thread Id= {Thread.CurrentThread.ManagedThreadId}");








            string resultPrefix = "", resultPostfix = "";
            StringBuilder jsonResultBuilder = new StringBuilder(resultPrefix + "[");
            for (int i = 0; i < allAccInfos.Count; i++)
            {
                AccInfo accInfo = allAccInfos[i];
                if (i != 0)
                    jsonResultBuilder.AppendFormat(",");
                jsonResultBuilder.Append($"{{\"BrAcc\":\"{accInfo.BrAccStr}\"");
                jsonResultBuilder.Append($",\"Timestamp\":\"{DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")}\"");
                jsonResultBuilder.Append($",\"AccSums\":[");
                for (int j = 0; j < accInfo.AccSums.Count; j++)
                {
                    AccSum accSum = accInfo.AccSums[j];
                    if (j != 0)
                        jsonResultBuilder.AppendFormat(",");
                    jsonResultBuilder.Append($"{{\"Tag\":\"{accSum.Tag}\",\"Value\":\"{accSum.Value}\",\"Currency\":\"{accSum.Currency}\"}}");
                }
                jsonResultBuilder.Append($"]");
                jsonResultBuilder.Append($",\"AccPoss\":[");
                bool isAnyRowWritten = false; int nWarnEst = 0;
                for (int j = 0; j < accInfo.AccPoss.Count; j++)
                {
                    AccPos accPos = accInfo.AccPoss[j];
                    if (accPos.IsHidingFromClient)
                        continue;
                    if (isAnyRowWritten)
                        jsonResultBuilder.AppendFormat(",");
                    isAnyRowWritten = true;

                    string symbol = accPos.Contract.Symbol; 
                    if (symbol == "BRK B")  // IB uses with space, but in our database it is with hyphen. There can be other differences. It is better to instantly convert these in VBroker before passing it to clients of Webserver, SQDesktop
                        symbol = "BRK-B";

                    jsonResultBuilder.Append($"{{\"Symbol\":\"{symbol}\",\"SecType\":\"{accPos.Contract.SecType}\",\"Currency\":\"{accPos.Contract.Currency}\",\"Pos\":\"{accPos.Position}\",\"AvgCost\":\"{accPos.AvgCost:0.00}\"");
                    if (accPos.Contract.SecType == "OPT")
                        jsonResultBuilder.Append($",\"LastTradeDate\":\"{accPos.Contract.LastTradeDateOrContractMonth}\",\"Right\":\"{accPos.Contract.Right}\",\"Strike\":\"{accPos.Contract.Strike}\",\"Multiplier\":\"{accPos.Contract.Multiplier}\",\"LocalSymbol\":\"{accPos.Contract.LocalSymbol}\"");

                    if (isNeedEstPr)
                    {
                        if (accPos.EstPrice<0.00001 && accPos.EstPrice > -0.00001)  // if it is zero
                        {
                            nWarnEst++;
                            //Console.WriteLine($"Warn.\"LocalSymbol\":\"{accPos.Contract.LocalSymbol}\": \"EstPrice\":\"{accPos.EstimatedPrice:0.00}\",\"EstUnderlyingPrice\":\"{accPos.EstimatedUnderlyingPrice:0.00}\" ");
                            Utils.Logger.Warn($"Warn({nWarnEst}).LocalS:{accPos.Contract.LocalSymbol}: EstP:{accPos.EstPrice:0.00},EstUnderlyingP:{accPos.EstUndPrice:0.00}");
                        }
                        jsonResultBuilder.Append($",\"EstPrice\":\"{accPos.EstPrice:0.00}\"");
                        if (accPos.Contract.SecType == "OPT")
                            jsonResultBuilder.Append($",\"EstUnderlyingPrice\":\"{accPos.EstUndPrice:0.00}\",\"IbComputedImpVol\":\"{accPos.IbComputedImpVol:0.000}\",\"IbComputedDelta\":\"{accPos.IbComputedDelta:0.000}\",\"IbComputedUndPrice\":\"{accPos.IbComputedUndPrice:0.00}\"");
                    }
                    jsonResultBuilder.Append($"}}");
                    //break; // temp here
                }
                jsonResultBuilder.Append($"]");

                jsonResultBuilder.Append($"}}");
            }

            jsonResultBuilder.Append(@"]" + resultPostfix);
            string result = jsonResultBuilder.ToString();
            Utils.Logger.Info($"GetAccountsInfo() END with result '{result}'");
            //Console.WriteLine($"GetAccountsInfo() END with result '{result}'");
            return result;
        }

        // >2019-03: After Market close: IB doesn't  give price for some 2-3 stocks (VXZ (only gives ask = -1, bid = -1), URE (only gives ask = 61, bid = -1), no open,low/high/last, not even previous Close price, nothing), these are the ideas to consider: We need some kind of estimation, even if it is not accurate.
        //     >One idea: ask IB's historical data for those missing prices. Then price query is in one place, but we have to wait more for VBroker, and IB throttle (max n. number of queries) may cause problem, so we get data slowly.
        //     >Betteridea: in Website, where the caching happens: ask our SQL database for those missing prices. We can ask our SQL parallel to the Vb query. No throttle is necessary. It can be very fast for the user. Prefer this now. More error proof this solution.
        private void CollectEstimatedPrices(List<AccInfo> allAccInfos, bool p_isNeedOptDelta, string[] p_addPrInfoSymbolsArr)
        {
            //Position.U407941 - Symbol: VXX, SecType: STK, Currency: USD, Position: -87, Avg cost: 21.1106586, LocalSymbol: 'VXX'
            //Position.U407941 - Symbol: VXX, SecType: OPT, Currency: USD, Position: 3, Avg cost: 780.35113335
            //    Option.LastTradeDate: 20181221, Right: C, Strike: 37, Multiplier: 100, LocalSymbol: 'VXX   181221C00037000'
            // Contract.ConID is inique integer. But for options of the same underlying ConID is different, so we cannot use ConID. We have to group it by stocks.
            bool isInRegularUsaTradingHoursNow = Utils.IsInRegularUsaTradingHoursNow(TimeSpan.FromDays(3));

            // 1. Add p_addPrInfoSymbolsArr additional tickers to the first AccInfo positions as 0 positions
            int ourFakeContractIdSeed = -1;
            foreach (var addPrTicker in p_addPrInfoSymbolsArr)
            {
                // if ticker is not in the list, add it as a size = 0, cost = 0 item. If it is in the list, don't add it.
                bool isTickerInAnyPosList = false;
                foreach (var accInfo in allAccInfos)
                {
                    if (accInfo.AccPoss.Exists(r => r.Contract.SecType == "STK" && r.Contract.Symbol == addPrTicker))
                    {
                        isTickerInAnyPosList = true;
                        break;
                    }
                }
                if (!isTickerInAnyPosList)
                {
                    Contract cont = VBrokerUtils.ParseSqTickerToContract(addPrTicker);
                    // generate mockup ConID, because we will differentiate them later by this. IB uses only big positive values, we can use negative.
                    allAccInfos[0].AccPoss.Add(new AccPos() { Contract = cont, FakeContractID = ourFakeContractIdSeed--, Position = 0.0, AvgCost = 0.0 });
                }
            }

            // 2. Create knownConIds dictionary that aggregates all Broker Accounts (so if QQQ is in all 3 of them, only once is queried for price)
            Dictionary<int, List<AccPos>> knownConIds = new Dictionary<int, List<AccPos>>();    // ContracdId to AccPos list.
            foreach (var accInfo in allAccInfos)
            {
                foreach (var pos in accInfo.AccPoss)
                {
                    int conId = pos.Contract.ConId;
                    if (conId == 0)
                        conId = pos.FakeContractID;
                    if (!knownConIds.TryGetValue(conId, out List<AccPos> poss))
                    {
                        knownConIds[conId] = new List<AccPos>() { pos };
                    }
                    else
                    {
                        poss.Add(pos);
                    }
                }
            }

            // 3. If it is an option, and the underlying is still not present, add it for price query. 
            foreach (var accInfo in allAccInfos)
            {
                foreach (var optPos in accInfo.AccPoss)
                {
                    if (optPos.Contract.SecType == "OPT")      // can be option on stocks or option on futures (VIX)
                    {
                        string underlyingSymbol = optPos.Contract.Symbol;
                        // search for underlying's in knownConIds, if not found create it and put it into the list.
                        // Now, we only handle options on Stocks, not options on VIX futures. For VIX futures options we will return UnderlyingEstPrice = 0. Fine, now.
                        var underlyingDictItem = knownConIds.FirstOrDefault(r => r.Value[0].Contract.SecType == "STK" && r.Value[0].Contract.Symbol == underlyingSymbol);
                        if (underlyingDictItem.Value != null)   // The FirstOrDefault method returns a KeyValuePair<string, int> which is a value type, so it cannot ever be null.
                        {   // we have found the underlying stock behind the option
                            optPos.UnderlyingDictItem = underlyingDictItem;
                        }
                        else
                        {   // create a new KeyValuePair in knownConIds, so later RtPrice should be queried for that too
                            if (underlyingSymbol == "VIX")  // Skip Futures now.
                                continue;
                            var contractUnd = VBrokerUtils.ParseSqTickerToContract(underlyingSymbol); // that is a Contract, but without a ContractID, but because IB ConId are positive we can use negative values
                            if (underlyingSymbol == "SVXY1")        // temporary. SVXY1 is a result of a split. Will expire when all the options expire.
                                contractUnd.Exchange = "BASKET";

                            List<AccPos> poss = new List<AccPos>() { new AccPos() { Contract = contractUnd, Position = 0, AvgCost = 0 } };
                            knownConIds[ourFakeContractIdSeed] = poss;
                            optPos.UnderlyingDictItem = new KeyValuePair<int, List<AccPos>>(ourFakeContractIdSeed--, poss);
                        }
                    }

                }
            }


            // IB pacing restriction. TWS:  50 requests / sec, IB gateway: ~120 requests / sec
            // Another way: we can send the first 50 query instantly, then we have to wait 1 seconds and do it in another burst. That would be better if we have only 49 stocks than Sleeping after each.
            // If Sleep every time, we can query 20 tickers per sec, 100 needs 5 sec. However, if we do a burst of 50 once, second burst of 50 can bring 100 in 2 seconds.

            // try to avoid pacing violation at TWS", the above lousy "sleep" is in milliseconds assures the ReqMktDataStream() happens only 50 times per second
            //int c_IbPacingViolationSleepMs = 1000 / 1000;  // MTS.TWS: first time OK, second time: "Max rate of messages per second has been exceeded:max=50 rec=161", so target 120
            //int c_IbPacingViolationSleepMs = 1000 / 120; // MTS.TWS: first time OK, second time: "Max rate of messages per second has been exceeded:max=50 rec=134", so target 90
            //int c_IbPacingViolationSleepMs = 1000 / 90; // MTS.TWS: first,second,third time OK, fourth time: "Max rate of messages per second has been exceeded:max=50 rec=58", so target 60
            //int c_IbPacingViolationSleepMs = 1000 / 60; // MTS.TWS: first,second,third time OK, fourth time: "Max rate of messages per second has been exceeded:max=50 rec=51", so target 50
            //int c_IbPacingViolationSleepMs = 1000 / 50;     // " Warning: Approaching max rate of 50 messages per second (48)". But if I query it quickly, even with this it can give an exceeded rec=71. So, don't start the next query too quickly.
            int c_IbPacingViolationSleepMs = 1000 / 40; // " Warning: Approaching max rate of 50 messages per second (48)", and I tried to be very quick, but I couldn't do error. Good. Keep this.   Queried: 96; sending time for Est prices and CancelMktData: 2*2380 ms

            // Loop over pairs with foreach.
            bool isStreamMktData = false;       // IbMarkPrice works only in stream mode, not in snapshot.
            DateTime waitEstPrStartTime = DateTime.UtcNow;
            AutoResetEvent priceOrDeltaTickARE = new AutoResetEvent(false);    // set it to non-signaled => which means Block
            int nKnownConIdsPrQueried = 0, nKnownConIdsPrReadyOk = 0, nKnownConIdsPrReadyErr = 0;
            int nKnownConIdsDeltaQueried = 0, nKnownConIdsDeltaReadyOk = 0, nKnownConIdsDeltaReadyErr = 0;
            foreach (KeyValuePair<int, List<AccPos>> pair in knownConIds)
            {
                List<AccPos> poss = pair.Value;
                Contract contract = poss[0].Contract;   // use the first AccPos item in the list as the representative
                if ((contract.SecType == "WAR") ||
                    (!String.IsNullOrEmpty(contract.Exchange) && (contract.Exchange == "CORPACT" || contract.Exchange == "VALUE" || contract.Exchange == "PINK")) ||
                    (contract.Currency != "USD"))
                {
                    // 2018-12:  Instead of about 150 positions, we decrease it to 96.  Helps in IB pacing restriction (50 msg per sec).
                    // Warrants and "CORPACT" exchange stocks are not important for us. For the sake of being brief and save time, we don't return them to the caller.
                    // CORPACT stocks: INNL.CVR or EDMC.WAR. (a STK). Last ClosePrice comes only after 5 sec. It doesn't worth to wait for it. Skip them.
                    // VALUE stocks: 2 out of 7 Value stocks, KWKAQ, IRGTQ, return no Ask/Bid/Last/IbMarkPrices. Only return LastClose prices 4 seconds after query. It doesn't worth waiting for them.
                    // PINK stocks: 4 out of 7 PINK stocks, RGDXQ, HGGGQ, DXIEF, IMRSQ After MarketClose: only have LastClosePrice, and not LastPrice or IbMarkPrice, so they are missing.
                    // We can accept PINK's LastClose quickly without delay, but better to decrease number of stocks for IB pacing restriction (50 msg per sec).
                    // Currency: EUR, Exchange: SBF: Like GNF, After Market Close only MarketDataType=2(Frozen) came, then nothing. During market hour it worked. Anyway, small position. Ignore.
                    Utils.Logger.Trace($"GetAccountsInfo(). RT prices. Skipping not important troublesome stock: {contract.Symbol} SecTye: {contract.SecType}, Exchange:{contract.Exchange}");
                    poss[0].IsHidingFromClient = true;
                    poss[0].EstPrice = 0.0;
                    continue;
                }

                // If we send contID, we still have to send the Exchange. (stupid, but it works that way). So what is the point of ContractId then if it is not used?
                // Contract.Exchange cannot be left empty, so if it is empty (like with options), fill with SMART
                // Other exchanges, like ARCA, CORPACT, VALUE works fine. Leave them.
                // change NASDAQ only to SMART too, because all NASDAQ stocks (TQQQ, BIB, TLT) return ReqMktDataStream() returns error: ErrCode: 200, Msg: No security definition has been found for the request;
                // examples: ONVO,TLT: NASDAQ, TMF: ARCA, options: null, INNL.CVR: CORPACT
                if (String.IsNullOrEmpty(contract.Exchange) || contract.Exchange == "NASDAQ" || contract.Exchange == "PINK")
                    contract.Exchange = "SMART";

                //var contract2 = VBrokerUtils.ParseSqTickerToContract(contract.Symbol);

                //if (!(contract.SecType == "STK" || contract.SecType == "OPT"))
                //    continue;

                nKnownConIdsPrQueried++;
                if (contract.SecType == "OPT")
                    nKnownConIdsDeltaQueried++;
                Thread.Sleep(c_IbPacingViolationSleepMs);    // OK.  "Waiting for RT prices: 396.23 ms. Queried: 96; AllUser_AccPos_WithEstPrices ends in 5277ms". 
                int mktDataId = m_mainGateway.BrokerWrapper.ReqMktDataStream(contract, isStreamMktData ? "221" : null, !isStreamMktData,  // if data is streamed continously and then we ask one snapshot of the same contract, snapshot returns currently, and stream also correctly continous later. As expected.
                    (cb_mktDataId, cb_mktDataSubscr, cb_tickType, cb_price) =>
                    {
                        Utils.Logger.Trace($"{cb_mktDataSubscr.Contract.Symbol} : {TickType.getField(cb_tickType)}: {cb_price}");

                        // Discussion: last price would be fine for stocks usually, but midPrice of askBid is needed for Options, because Last can be half a day ago. So, do midPrice in general, except for "IND" where only Last is possible
                        // "STK" or "OPT": MID is the most honest price. LAST may happened 1 hours ago
                        //  far OTM options (VIX, VXX) have only Ask, but no Bid. Estimate missing by zero.
                        // After market hour, even liquid stocks (PM, IBKR) doesn't return Ask,Bid. And we have to wait after the 5 second timeout, when they do TickSnapshotEnd, and just before that they send Trace: IBKR : bidPrice: -1.
                        // so, for stocks. (especially AMC), we should accept LastPrice.
                        // Using LastPrice instead of Ask,Bid for stocks changed that this query of 58 contracts returns in 300msec, instead of 900msec + the possibility of 5second timeout.
                        // That is a very good reason to use the LastPrice.
                        // Or use MarkPrice for stocks. MARK_PRICE (Mark Price (used in TWS P&L computations)): can be calculated.
                        //      The mark price is equal to the LAST price unless:
                        //      Ask < Last - the mark price is equal to the ASK price.
                        //      Bid > Last - the mark price is equal to the BID price.
                        // !!Using IbMarkPrice: query of 58 contracts returns in 130msec, so it is twice as fast as getting LastPrice
                        // For IbMarkPrice. Snapshot gives error. I have to do Stream to get IBMarkPrice. Fine. 
                        //      But with stream, we have to CancelMktData, which is a problem if we do 93 queries, because  IB pacing restriction (TWS: 50 requests/sec)
                        // However, at the weekends or on holidays, IBservers are down, and they don't give IbMarkPrice. So, we have to use LastPrice anyway.
                        // Indices only have LastPrice, no Ask,Bid
                        // Decision: 
                        // 1. for stocks: Don't waith for AskBid, because it may never given or comes only at TickSnapshotEnd after 5 sec. 
                        // one option is to use LastPrice, which is there always. However, it may be too far from a realistic ask-bid MidPrice after Market close.
                        // The best is use IbMarkPrice estimate, because I checked and it is reliably given for all stocks. And it is more honest than LastPrice.
                        // However, because of IB pacing restriction, we should prefer Snapshot, which doesn't allow IbMarkPrice. Let's do this for now.
                        // Note that at the weekend, even liquid stocks (IDX,AFK,PM,IBKR,VBR) in snapshot mode or stream returns nothing. (Just MarketDataType(2first,1later), not even LastPrice, or IbMarkPrice).
                        // In that case, there is nothing to do but return 0. But the question is how can TWS estimate the MktVal then? 
                        // TODO: Maybe TWS uses historicalData (not realtime) then when we don't have RT data, but it would delay the query again. So, skip it for now.
                        // During market hours, which is important we can return the correct data.
                        // 2. for options: IbMarkPrice is not available, LastPrice can be too old, so the only way to use AskBid

                        // AskBid is good for both options and stocks if it is given.
                        // we have to use it even for stocks. TMF stock, even in regular trading hours, on AutoTraderServer: only askBid comes, no LastPr. 
                        // However, on ManualTradingServer (Ireland), it is not a problem. So, we have to use AskBid for stocks for Agy account.
                        // TODO: if both Ask,Bid = -1 (no data), use PrClose price, which is given many times.
                        if ((cb_tickType == TickType.ASK) || (cb_tickType == TickType.BID))
                        {
                            if (cb_tickType == TickType.ASK)
                                poss[0].AskPrice = cb_price;
                            if (cb_tickType == TickType.BID)
                                poss[0].BidPrice = cb_price;

                            // some stocks have proper LastPrice and MarkPrice, but later ask/bid comes as -1/-1 meaning no ask-bid. And because we round that -1 to 0 properly, we have a proposedPrice = 0.
                            // assure that a '0' proposedPrice will not overwrite a previously correct lastPrice 
                            // sometimes Ask or Bid that comes is -1 (even for stock MVV), which shows that Bid is missing (nobody is willing to buy). For non-liquid options, this is acceptable. Round them to 0, or use LastTradedPrice in that case
                            // But if both ask and bid is -1, then don't accept the price, but wait for more data
                            bool isAskBidAcceptable = (!Double.IsNaN(poss[0].AskPrice)) && (!Double.IsNaN(poss[0].BidPrice));
                            if (isAskBidAcceptable)
                            {
                                isAskBidAcceptable = (poss[0].AskPrice != -1.0) && (poss[0].BidPrice != -1.0);
                                if (isAskBidAcceptable && !isInRegularUsaTradingHoursNow)
                                {   // sometimes before premarket: ask: 8.0 Bid: 100,000.01. In that case, don't accept it as a correct AskBid
                                    // but BRK.A is "340,045" and legit. So, big values should be accepted if both Ask, Bid is big.
                                    // if it is premarket, check that their difference, the AskBid spread is also small. If not, ignore them (and later LastClose will be given back)
                                    // NOTE: maybe this should be considered an error, no matter if it is isInRegularUsaTradingHoursNow or not.
                                    isAskBidAcceptable = (Math.Abs(Math.Abs(poss[0].AskPrice) - Math.Abs(poss[0].BidPrice)) < 90000);
                                }
                            }
                            if (isAskBidAcceptable)
                            {
                                double pAsk = (poss[0].AskPrice < 0.0) ? 0.0 : poss[0].AskPrice;
                                double pBid = (poss[0].BidPrice < 0.0) ? 0.0 : poss[0].BidPrice;
                                double proposedPrice = (pAsk + pBid) / 2.0;
                                if (Double.IsNaN(poss[0].EstPrice))    // only increase the nKnownConIdsPrReadyOk counter once when we turn from NaN to a proper number.
                                {
                                    poss[0].EstPrice = proposedPrice;
                                    nKnownConIdsPrReadyOk++;
                                    priceOrDeltaTickARE.Set();
                                }
                                else
                                {
                                    poss[0].EstPrice = proposedPrice;    // update it with new value
                                }
                            }
                        }

                        if ((cb_mktDataSubscr.Contract.SecType == "IND") || (cb_mktDataSubscr.Contract.SecType == "STK"))
                        {
                            if (cb_mktDataSubscr.Contract.Exchange == "PINK")   // even though we ignore PINK, we may need them in the future.
                            {
                                //For PINK stocks, especially AMC, If MarketDataType() = 2 (Frozen), we should accept LastClosePrice as Estimated price.
                                if (cb_tickType == TickType.CLOSE && Double.IsNaN(poss[0].EstPrice))    // only increase the nKnownConIdsPrReadyOk counter once when we turn from NaN to a proper number.
                                {
                                    poss[0].EstPrice = cb_price;
                                    nKnownConIdsPrReadyOk++;
                                    priceOrDeltaTickARE.Set();
                                }
                            }

                            // Store both IbMarkPrice and LastPrice too. And any of them is fine. Sometimes both are given, but at the weekend only LastPrice is given no IbMarkPrice. So, we cannot rely on that. 
                            // However, until we wait for other prices, maybe we got the better MarkPrice. If it is given, use the MarkPrice, otherwise the LastPrice. Store both temporarily.
                            if ((cb_tickType == TickType.MARK_PRICE) || (cb_tickType == TickType.LAST))
                            {
                                if (cb_tickType == TickType.MARK_PRICE)
                                {
                                    poss[0].IbMarkPrice = cb_price;
                                }
                                if (cb_tickType == TickType.LAST)
                                {
                                    poss[0].LastPrice = cb_price;
                                }
                                if (Double.IsNaN(poss[0].EstPrice))    // only increase the nKnownConIdsPrReadyOk counter once when we turn from NaN to a proper number.
                                {
                                    poss[0].EstPrice = cb_price;
                                    nKnownConIdsPrReadyOk++;
                                    priceOrDeltaTickARE.Set();
                                }
                                else
                                {
                                    if (cb_tickType == TickType.MARK_PRICE) // only MarkPrice can overwrite it, but LastPrice not (once it is filled up)
                                        poss[0].EstPrice = cb_price;
                                }
                            }
                        }                    
                    },
                    (cb_mktDataId, cb_mktDataSubscr, cb_errorCode, cb_errorMsg) =>
                    {
                        Utils.Logger.Trace($"Error in ReqMktDataStream(). {cb_mktDataSubscr.Contract.Symbol} : {cb_errorCode}: {cb_errorMsg}");
                        poss[0].EstPrice = 0;
                        nKnownConIdsPrReadyErr++;
                        priceOrDeltaTickARE.Set();
                    },
                    (cb_mktDataId, cb_mktDataSubscr, cb_field, cb_value) => // TickGeneric() callback. Assume this is the last callback for snapshot data. (!Not true for OTC stocks, but we only use this for options) Note sometimes it is not called, only Ask,Bid is coming.
                    {  // Tick Id:1222, Field: halted, Value: 0
                        Utils.Logger.Trace($"TickGeneric in ReqMktDataStream(). {cb_mktDataSubscr.Contract.Symbol} : {TickType.getField(cb_field)}: {cb_value}");
                        if ((cb_mktDataSubscr.Contract.SecType == "IND") || (cb_mktDataSubscr.Contract.SecType == "STK"))
                        { // do nothing. LastPrice is already filled
                        }
                        else // SecType == "OPT"
                        {   // maybe only Ask or only Bid was given (correctly, because they don't exist at all for far OTM options)
                            if (Double.IsNaN(poss[0].EstPrice))
                            {
                                double pAsk = (Double.IsNaN(poss[0].AskPrice)) ? 0.0 : poss[0].AskPrice;
                                double pBid = (Double.IsNaN(poss[0].BidPrice)) ? 0.0 : poss[0].BidPrice;
                                double proposedPrice = (pAsk + pBid) / 2.0;
                                if (Double.IsNaN(poss[0].EstPrice))    // only increase the nKnownConIdsPrReadyOk counter once when we turn from NaN to a proper number.
                                {
                                    poss[0].EstPrice = proposedPrice;
                                    nKnownConIdsPrReadyOk++;
                                    priceOrDeltaTickARE.Set();
                                }
                                else
                                    poss[0].EstPrice = proposedPrice;    // update it with new value
                            }
                        }
                    },
                    (cb_mktDataId, cb_mktDataSubscr, cb_type) =>
                    {
                        Utils.Logger.Trace($"MarketDataType in ReqMktDataStream(). {cb_mktDataSubscr.Contract.Symbol} : {cb_type}");
                        // TMF, VXX can be Frozen(2) too after market close, or at weekend. It means that sometimes there is no more price data. So, we should signal to clients that don't expect more data. Don't wait.
                        // However, 95% of the cases there is proper market data even in this case
                        // weird notice AfterMarketClose: for TMF, VXX, SVXY stocks: MarketDataType(2) first, then MarketDataType(1), then nothing. Other stocks: only MarketDataType(2), then proper Last,Ask,Bid prices
                        // this may means that we started StreamingDataType(2), but later IB realized it is impossible, so changed it to FrozenHistorical (DataType=1)
                        if (cb_mktDataSubscr.PreviousMktDataType == 2 && cb_type == 1)
                        {
                            // Note that at the weekend, even liquid stocks (IDX,AFK,PM,IBKR,VBR) in snapshot mode or stream returns nothing. (Just MarketDataType(2first,1later), not even LastPrice, or IbMarkPrice).
                            // In that case, there is nothing to do but return 0. But the question is how can TWS estimate the MktVal then? 
                            // TODO: Maybe TWS uses historicalData then.
                            if (Double.IsNaN(poss[0].EstPrice))    // only increase the nKnownConIdsPrReadyOk counter once when we turn from NaN to a proper number.
                            {
                                Utils.Logger.Trace($"MarketDataType in ReqMktDataStream(). Filling zero estimation for {cb_mktDataSubscr.Contract.Symbol}");
                                poss[0].EstPrice = 0;
                                nKnownConIdsPrReadyOk++;
                                priceOrDeltaTickARE.Set();
                            }
                        }
                    },
                    (cb_mktDataId, cb_mktDataSubscr, cb_field, cd_impVol, cb_delta, cb_undPrice) => // TickGeneric() callback. Assume this is the last callback for snapshot data. (!Not true for OTC stocks, but we only use this for options) Note sometimes it is not called, only Ask,Bid is coming.
                    {  // Tick Id:1222, Field: halted, Value: 0
                        Utils.Logger.Trace($"TickOptionComputation in ReqMktDataStream(). {cb_mktDataSubscr.Contract.Symbol} : {TickType.getField(cb_field)}: {cb_delta}, {cb_undPrice}");
                        if ((cb_mktDataSubscr.Contract.SecType == "IND") || (cb_mktDataSubscr.Contract.SecType == "STK"))
                        { // do nothing. LastPrice is already filled
                        }
                        else // SecType == "OPT"
                        {  // TickOptionComputation() comes 4 times with 4 different IV; the lastly reported Delta seems to be the best.
                            // if (cd_impVol != "1.79769313486232E+308")  = Double.MaxValue, but if that comes, then don't replace the last Delta values
                            // if cb_undPrice = 0.0, then every Greek value is computed wrongly, Gamma =0, Vega = 0, Delta = -1. That is a faulty data given after MOC.
                            if (cb_delta != Double.MaxValue && cb_undPrice != 0.0)
                            {
                                if (Double.IsNaN(poss[0].IbComputedDelta))    // only increase the nKnownConIdsPrReadyOk counter once when we turn from NaN to a proper number.
                                {
                                    Utils.Logger.Trace($"TickGeneric() callback. Filling zero Delta estimation for {cb_mktDataSubscr.Contract.Symbol}");
                                    poss[0].IbComputedDelta = cb_delta;
                                    nKnownConIdsDeltaReadyOk++;
                                    priceOrDeltaTickARE.Set();
                                } else
                                    poss[0].IbComputedDelta = cb_delta;
                            }
                            if (cd_impVol != Double.MaxValue)
                                poss[0].IbComputedImpVol = cd_impVol;
                            if (cb_undPrice != Double.MaxValue && cb_undPrice != 0.0) // after market close, even QQQ option returns this nonsense than UnderlyingPrice = 0 or Double.MaxValue. Ignore that.
                                poss[0].IbComputedUndPrice = cb_undPrice;
                        }
                                });    // as Snapshot, not streaming data
                pair.Value[0].MktDataID = mktDataId;    // use the first AccPos item in the list as the representative
            }  // foreach knownConIds
            string msg1 = $"ReqMktData() sending time for Est prices: {(DateTime.UtcNow - waitEstPrStartTime).TotalMilliseconds:0.00} ms. Queried: {nKnownConIdsPrQueried}, ReceivedOk: {nKnownConIdsPrReadyOk}, ReceivedErr: {nKnownConIdsPrReadyErr}, Missing: {nKnownConIdsPrQueried - nKnownConIdsPrReadyOk - nKnownConIdsPrReadyErr}";
            Utils.Logger.Trace(msg1);  // RT prices: After MOC:185.5ms, during Market:FirstTime:900-1100ms,Next times:450-600ms, Local development all 58 stocks + options, Queried: 58, ReceivedOk: 57, ReceivedErr: 1, Missing: 0,  
            Console.WriteLine(msg1);

            // instead of Thread.Sleep(3000);  // wait until data is here; make it sophisticated.
            // for 23 stocks, 0 options, LocalDevelopment, collecting RT price: 273-300ms
            //Thread.Sleep(3000);
            int iTimeoutCount = 0;
            int cMaxTimeout = 30;   //  15*400=6sec was not enough. Needed another 5sec for option Delta calculation. So, now do 30*400=12sec.
            while (iTimeoutCount < cMaxTimeout)    // all these checks usually takes 0.1 seconds = 100msec, so do it every time after connection 
            {
                bool isOneSignalReceived = priceOrDeltaTickARE.WaitOne(400);  // 400ms wait, max 6 times.
                if (isOneSignalReceived)   // so, it was not a timeout, but a real signal
                {
                    var waitingDuration = DateTime.UtcNow - waitEstPrStartTime;
                    if (waitingDuration.TotalMilliseconds > 8000.0)
                        break;  // if we wait more than 8 seconds, break the While loop


                    bool isAllPrInfoReceived = ((nKnownConIdsPrReadyOk + nKnownConIdsPrReadyErr) >= nKnownConIdsPrQueried);
                    bool isAllDeltaInfoReceived = ((nKnownConIdsDeltaReadyOk + nKnownConIdsDeltaReadyErr) >= nKnownConIdsDeltaQueried);
                    if (isAllPrInfoReceived)    // break from while if all is received
                    {
                        // local Windoms TWS: "Time until having all EstPrices (and Deltas): "
                        // Only 37 prices: 1409.78 ms,  37 prices + 12 option deltas: 1762.84 ms, so we have to wait extra 300msec if we wait for option Deltas too. Fine. However, after MOC, on TWS Main, it took 11 seconds more to get all the Deltas.
                        if (!p_isNeedOptDelta)
                            break;
                        if (isAllDeltaInfoReceived)
                            break; 
                    }
                }
                else
                {
                    iTimeoutCount++;
                    int nMissingLastPrice = 0;
                    foreach (KeyValuePair<int, List<AccPos>> pair in knownConIds)
                    {
                        List<AccPos> poss = pair.Value;
                        if (Double.IsNaN(poss[0].EstPrice))     // These shouldn't be here. These are the Totally Unexpected missing ones or the errors. Estimate missing numbers with zero.
                        {
                            nMissingLastPrice++;
                            Utils.Logger.Trace($"Missing: '{poss[0].Contract.LocalSymbol}', {poss[0].MktDataID}");
                        }
                    }
                    Utils.Logger.Trace($"GetAccountsInfo(). RT prices {iTimeoutCount}/{cMaxTimeout}x Timeout after {(DateTime.UtcNow - waitEstPrStartTime).TotalMilliseconds} ms. nReceived {nKnownConIdsPrReadyOk + nKnownConIdsPrReadyErr} out of {nKnownConIdsPrQueried}. nMissingLastPrice:{nMissingLastPrice}");
                }
            }
            string msg2 = $"Time until having all EstPrices (and Deltas): {(DateTime.UtcNow - waitEstPrStartTime).TotalMilliseconds:0.00} ms. Pr.Queried: {nKnownConIdsPrQueried}, ReceivedOk: {nKnownConIdsPrReadyOk}, ReceivedErr: {nKnownConIdsPrReadyErr}, Missing: {nKnownConIdsPrQueried - nKnownConIdsPrReadyOk - nKnownConIdsPrReadyErr}, Delta.Queried: {nKnownConIdsDeltaQueried}, ReceivedOk: {nKnownConIdsDeltaReadyOk}, ReceivedErr: {nKnownConIdsDeltaReadyErr}, Missing: {nKnownConIdsDeltaQueried - nKnownConIdsDeltaReadyOk - nKnownConIdsDeltaReadyErr}";
            Utils.Logger.Trace(msg2);  // RT prices: After MOC:185.5ms, during Market:FirstTime:900-1100ms,Next times:450-600ms, Local development all 58 stocks + options, Queried: 58, ReceivedOk: 57, ReceivedErr: 1, Missing: 0,  
            Console.WriteLine(msg2);


            foreach (KeyValuePair<int, List<AccPos>> pair in knownConIds)       // propagate the LastPrice from the first item to the others
            {
                List<AccPos> poss = pair.Value;
                if (Double.IsNaN(poss[0].EstPrice))     // These shouldn't be here. These are the Totally Unexpected missing ones or the errors. Estimate missing numbers with zero.
                {
                    Utils.Logger.Warn($"!GetAccInf().Unexpected no EstPr'{poss[0].Contract.LocalSymbol ?? poss[0].Contract.Symbol}'");

                    if (Double.IsNaN(poss[0].BidPrice))
                        poss[0].BidPrice = 0;
                    if (Double.IsNaN(poss[0].AskPrice))
                        poss[0].AskPrice = 0;
                    //poss[0].EstPrice = (poss[0].AskPrice + poss[0].BidPrice) / 2.0;
                    poss[0].EstPrice = 0.0; // Estimate missing numbers with zero.
                }

                if ((poss[0].Contract.SecType == "OPT") && Double.TryParse(poss[0].Contract.Multiplier, out double multiplier))  // Options: Cost given by IB is multiplied by the Multiplier. It makes sense that the EstPrice of 1 option is also given back like that.
                    poss[0].EstPrice *= multiplier;  

                poss[0].EstUndPrice = 0.0;
                if (poss[0].Contract.SecType == "OPT" && poss[0].UnderlyingDictItem.Value != null)
                {
                    poss[0].EstUndPrice = poss[0].UnderlyingDictItem.Value[0].EstPrice;

                }
                for (int i = 1; i < poss.Count; i++)
                {
                    poss[i].EstPrice = poss[0].EstPrice;
                    poss[i].EstUndPrice = poss[0].EstUndPrice;
                    poss[i].IbComputedImpVol = poss[0].IbComputedImpVol;
                    poss[i].IbComputedDelta = poss[0].IbComputedDelta;
                    poss[i].IbComputedUndPrice = poss[0].IbComputedUndPrice;
                }

            }


            // we should cancel even the snapshot mktData from BrokerWrapperIb, because the field m_MktDataSubscription is just growing and growing there.
            DateTime cancelDataStartTime = DateTime.UtcNow;
            foreach (KeyValuePair<int, List<AccPos>> pair in knownConIds)
            {
                if (pair.Value[0].MktDataID != -1)
                {
                    if (isStreamMktData)    // if snapshot data, then no msg is sent to IB at Cancellation.
                        Thread.Sleep(c_IbPacingViolationSleepMs);
                    m_mainGateway.BrokerWrapper.CancelMktData(pair.Value[0].MktDataID);
                }
            }
            string msg3 = $"CancelMktData (!we can do it later) in {(DateTime.UtcNow - cancelDataStartTime).TotalMilliseconds:0.00} ms";
            Utils.Logger.Trace(msg3);  // RT prices: After MOC:185.5ms, during Market:FirstTime:900-1100ms,Next times:450-600ms, Local development all 58 stocks + options, Queried: 58, ReceivedOk: 57, ReceivedErr: 1, Missing: 0,  
            Console.WriteLine(msg3);
        }  // CollectEstimatedPrices()

        private void FindGatewayAndAdd(GatewayUser[] p_possibleGwUsers, AccInfo p_accSumPos)
        {
            foreach (var gwUser in p_possibleGwUsers)
            {
                Gateway gw = m_gateways.Find(r => r.GatewayUser == gwUser);
                if (gw != null && gw.IsConnected)   // only add the connected gateways
                {
                    p_accSumPos.Gateway = gw;
                    return;
                }
            }
        }
    }
}
