using DbCommon;
using IBApi;
using SqCommon;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Utils = SqCommon.Utils;

namespace VirtualBroker
{
    public struct UberVxxQuoteData
    {
        public DateTime Date;
        public double AdjClosePrice;
        public double PctChg;

        //Offsets: we cannot aggregate Forward and Backward, because imagine that because of WW-III, there is only one day traded during the month, that day is Both a TotM+1, and TotM-1
        public int TotMForwardOffset;  // 1 = T+1
        public int TotMBackwardOffset; // 1 = T-1
        public int TotMidMForwardOffset;  // 1 = T+1    // it is safer to read the code if "Mid" is there
        public int TotMidMBackwardOffset; // 1 = T-1

    }

    // Portfolio specific parameters are here. User1's portfolio1 may use double leverage than User2's portfolio2. The Common Strategy params should go to StrategyConfig.cs
    public class PortfolioParamUberVXX : IPortfolioParam
    {
        public double PlayingInstrumentVixLongLeverage { get; set; }
        public double PlayingInstrumentVixShortLeverage { get; set; }
    }

    public partial class UberVxxStrategy : IBrokerStrategy
    {
        StringBuilder m_detailedReportSb;

        Task<Dictionary<string, Tuple<IAssetID, string>>> m_loadAssetIdTask = null;
        Dictionary<string, Tuple<IAssetID, string>> m_tickerToAssetId = null;   // ticker => AssetID : FullTicker mapping

        public bool IsSameStrategyForAllUsers { get; set; } = true;

        UberVxxConfig uberVxxConfig = new UberVxxConfig();
        // advice: if it is a fixed size, use array; faster; not list; List is painful to initialize; re-grow, etc. http://stackoverflow.com/questions/466946/how-to-initialize-a-listt-to-a-given-size-as-opposed-to-capacity
        // "List is not a replacement for Array. They solve distinctly separate problems. If you want a fixed size, you want an Array. If you use a List, you are Doing It Wrong."
        List<QuoteData> m_vxxQuotesFromSqlDB;   // doesn't contain today real-time price
        List<QuoteData> m_vxxQuotesFromIB;      // contains today real-time price too, but max IB history is for 1 year
        UberVxxQuoteData[] m_vxx;               // contains today real-time price too

        List<QuoteData> m_spyQuotesFromSqlDB;   // doesn't contain today real-time price
        UberVxxQuoteData[] m_spy;               // doesn't contain today real-time price, because it is not necessary for the calculation
        

        public static IBrokerStrategy StrategyFactoryCreate()
        {
            return new UberVxxStrategy();
        }

        public void Init(StringBuilder p_detailedReportSb)
        {
            m_detailedReportSb = p_detailedReportSb;
            m_loadAssetIdTask = Task.Run(() => DbCommon.SqlTools.LoadAssetIdsForTickers(new List<string>() {"VXX", "SVXY" }));   // task will start to run on another thread (in the threadpool)
        }

        public string StockIdToTicker(int p_stockID)
        {
            if (m_tickerToAssetId == null)
                m_tickerToAssetId = m_loadAssetIdTask.Result;   // wait until the parrallel task arrives
            return m_tickerToAssetId.First(r=> r.Value.Item1.ID == p_stockID).Key;
        }

        public IAssetID TickerToAssetID(string p_ticker)
        {
            if (m_tickerToAssetId == null)
                m_tickerToAssetId = m_loadAssetIdTask.Result;   // wait until the parrallel task arrives
            return m_tickerToAssetId[p_ticker].Item1;
        }

        public double GetPortfolioLeverage(List<PortfolioPositionSpec> p_suggestedPortfItems, IPortfolioParam p_param)
        {
            PortfolioParamUberVXX param = (PortfolioParamUberVXX)p_param;
            // the strategy knows that p_suggestedPortfItems only has 1 row, either VXX (for long VXX) or SVXY (for short VXX)
            if (p_suggestedPortfItems[0].Ticker == "VXX")
                return param.PlayingInstrumentVixLongLeverage;
            else
                return param.PlayingInstrumentVixShortLeverage;
        }

        public List<PortfolioPositionSpec> GeneratePositionSpecs(IPortfolioParam p_portfolioParam)
        {
            Utils.Logger.Info("UberVxxStrategy.GeneratePositionSpecs() Begin.");
            if (!GetHistoricalAndRealTimeDataForAllParts())
            {
                Utils.Logger.Error("UberVxxStrategy.GeneratePositionSpecs() GetHistoricalAndRealTimeDataForAllParts() Error. However, we try to continue. Maybe the problematic data will be not used in Forecast.");
            }
            double forecast = GetForecastVxx();

            Utils.ConsoleWriteLine(ConsoleColor.Green, false, $"Final VXX Forecast:{forecast * 100}%");
            Utils.Logger.Info($"Final VXX Forecast:{forecast * 100}%");
            m_detailedReportSb.AppendLine($"<font color=\"#10ff10\">Final VXX Forecast:{forecast * 100}%</font>");

            List <PortfolioPositionSpec> specs = new List<PortfolioPositionSpec>();
            if (forecast > 0)   // bullish on VXX: buy VXX or UVXY/TVIX
            {
                // IBrokerStrategy should not really know about the Trading Execution, or the proper trading instrument of the user or leverage of the user. 
                // The BrokerTask should overwrite the trading instrument if it wants that suits to the different portfolios
                // but we want to Calculate this complex Strategy only once, just giving a guideline for the BrokerTask
                specs.Add(new PortfolioPositionSpec() { Ticker = "VXX", PositionType = PositionType.Long, Size = WeightedSize.Create(1.0) });
            }
            else if (forecast < 0)  // bearish on VXX: short VXX or UVXY/TVIX
            {
                specs.Add(new PortfolioPositionSpec() { Ticker = "SVXY", PositionType = PositionType.Long, Size = WeightedSize.Create(1.0) });
            }
            else
                Utils.Logger.Warn("Stay in cash");

            return specs;
        }

        private bool GetHistoricalAndRealTimeDataForAllParts()
        {
            bool isOkGettingHistoricalData = true;
            // 1. Get VXX price data
            // we can get histical data from these sources and compare them
            // 1. IB: quickest, but 'Historical data request for greater than 365 days rejected.' and it is split adjusted, but if dividend is less than 10%, it is not adjusted.
            //      checked that: the today (last day) of IB.ReqHistoricalData() is not always correct. And it is not always the last real time price. It only works 90% of the time.
            //      2016-07-05: after a 3 days weekend: "Historical data end - 1001 from 20160105  13:50:01 to 20160705  13:50:01 ", and that time (before Market open), realtime price was 13.39.
            //         later on that day, it always give 13.39 for today's last price in ClientSocket.reqHistoricalData. So, don't trust the last day. Asks for a real time price separately from stream.
            // 2. YahooFinance as CSV, but twice every year YahooFinance doesn't have the data, or one date is missing from the 200 values
            // 3. GoogleFinance as HttpDownload
            // 4. Our SQL server.
            //// we assume our SQL DB is correct, it is always alive; and has the correct Split info in it, so AdjustedPrice is calculated correctly
            // YF sometimes is not correct. And if it is not correct, we cannot reapair it.
            // IB sometimes doesn't give back the LastClosePrice; it is bug in their system. We cannot fix it.
            // At least, if our SQL DB doesn't give back the info, we can correct it during the day. So, we should rely on our SQL DB more than YF.

            //var p_sqlConn = new SqlConnection("ConnectionString");
            int vxxLookbackWindowSize = 102;
            m_vxxQuotesFromSqlDB = SqlTools.LoadHistoricalQuotesAsync(new[] {
                    new QuoteRequest { Ticker = "VXX", nQuotes = vxxLookbackWindowSize }}, DbCommon.AssetType.Stock).Result.
                    Select(row => new QuoteData { Date = (DateTime)row[1], AdjClosePrice = (double)Convert.ToDecimal(row[2]) }).OrderBy(row => row.Date).ToList(); // stocks come as double objects: (double)row[2], indexes as floats  (double)(float)row[2]

            // check that the last date in the CSV is what we expect: the previous market Open day
            //DateTime expectedUtcLastDateTime = DBUtils.GetPreviousMarketOpenDay(utcNow, StockExchangeID.NASDAQ, VBroker.g_dbManager); // it needs UTC input, result.TimeOfDay == local 00:00 converted to UTC; so it is <Date>:4:00 (UTC), so we should convert it back to Local
            //DateTime expectedETLastDateTime = TimeZoneInfo.ConvertTimeFromUtc(expectedUtcLastDateTime, Utils.FindSystemTimeZoneById(TimeZoneId.EST));
            //DateTime expectedETLastDate = expectedETLastDateTime.Date;
            //VBrokerLogger.StrongAssert(taskLogFile, vxxQuotesFromSqlDB.Last().Date == expectedETLastDate, Severity.Exception, "m_vxxQuotesFromSqlDB.Last().Date == m_dateNowInET");             // assert that its last Date is today, VBroker is running all day

            //m_vxx = vxxQuotesFromSqlDB.Select(item => new QuoteData() { Date = item.Date, AdjClosePrice = item.AdjClosePrice }).ToList(); // Clone the SQL version, not YF

            // so, for VXX, for which there is no dividend, I can use the split-adjusted IB prices, but for other cases, I will have to use our SQL database (or YF or GF or all)
            Contract contract = VBrokerUtils.ParseSqTickerToContract("VXX");
            if (!Controller.g_gatewaysWatcher.ReqHistoricalData(DateTime.UtcNow, vxxLookbackWindowSize, "TRADES", contract, out m_vxxQuotesFromIB))   // real trades, not the MidPoint = AskBidSpread
            {
                isOkGettingHistoricalData = false;
                Utils.Logger.Error("VXX historical data was not given from IB within the 14seconds timeout. We continue, but it will be a problem later. If this problem continues, try to get historical VXX data from both IB and SQL DB, and use the one which returns within 5 seconds. We cannot delay this data, because we only have 15 seconds to do the trade before Market Closes.");
            }
            else
            {
                var rtPrices = new Dictionary<int, PriceAndTime>() { { TickType.MID, new PriceAndTime() } };    // MID is the most honest price. LAST may happened 1 hours ago
                StrongAssert.True(Controller.g_gatewaysWatcher.GetAlreadyStreamedPrice(contract, ref rtPrices), Severity.ThrowException, $"There is no point continuing if rtPrice cannot be obtained for ticker '{contract.Symbol}'.");
                double rtPrice = rtPrices[TickType.MID].Price;
                StrongAssert.True(Math.Abs((m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - 1].AdjClosePrice - rtPrice) / m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - 2].AdjClosePrice) < 0.006, Severity.NoException,  // should be less than 0.6%
                    $"VXX RT price from stream ({rtPrice}) is too far away from lastPrice ({m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - 1].AdjClosePrice}) from IB.ReqHistoricalData(), which usually is the RT price. We continue using RT stream price anyway. This usually happens after 3days weekends. No worries, replacing last historical price with RT stream price solves this perfectly. Make a comment into BrokerWrapperIb.cs/ReqHistoricalData() function in the source code with this example and date.");

                m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - 1] = new QuoteData() { Date = m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - 1].Date, AdjClosePrice = rtPrice };   // but we use the streamed rtPrice anyway

                // Check danger after stock split correctness: adjusted price from IB should match to the adjusted price of our SQL DB. Although it can happen that both data source is faulty.
                if (Utils.IsInRegularUsaTradingHoursNow(TimeSpan.FromDays(3)))
                {// in development, we often program code after IB market closed. Ignore this warning after market, but check it during market.
                    // 2016-06-27: VXX ClosePrice: YF historical: $16.83, all others: GF, Marketwatch, IB, YF main (not historical) page: $16.92
                    StrongAssert.True(Math.Abs(m_vxxQuotesFromSqlDB[m_vxxQuotesFromSqlDB.Count - 1].AdjClosePrice - m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - 2].AdjClosePrice) < 0.02, Severity.NoException,
                        $"Yesterday close price for {contract.Symbol} doesn't match between IB ({m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - 2].AdjClosePrice}) and SQL DB ({m_vxxQuotesFromSqlDB[m_vxxQuotesFromSqlDB.Count - 1].AdjClosePrice}). We continue trading and use only the more trustworthy IB data. If you check IB ClosePrice with other sources (GF, Marketwatch), and that is a good value, there is nothing to do. VBroker will use the IB historical price and IB ClosePrice.");
                }
                // log the last 3 values (for later debugging)
                Utils.Logger.Trace($"{m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - 3].Date.ToString("yyyy-MM-dd")}: {m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - 3].AdjClosePrice}");
                Utils.Logger.Trace($"{m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - 2].Date.ToString("yyyy-MM-dd")}: {m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - 2].AdjClosePrice}");
                Utils.Logger.Trace($"{m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - 1].Date.ToString("yyyy-MM-dd")}: {m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - 1].AdjClosePrice} (!Last trade, not last midPoint)");

                // 2. Do some preprocess
                //Implement that connor vbroker calculates sma100 of prob ft dir, and act according to a threshold
                //FTDirProb Regime Threshold:	0.47, >47% is good regime (for example 48% is still a good regime); but, 47.00 is a bed regime. In bad regime, we do MR, otherwise FT.
                //"g:\work\Archi-data\HedgeQuant\docs\gyantal\Studies\VIX\Autocorrelation\VXX auto-correlation with timers\probFTDirSMA100Timer\VXX auto-correlation-from2004_probFTDirSMA100Timer_doInverse 2.xlsx" 
                m_vxx = new UberVxxQuoteData[101];
                for (int i = 0; i < 101; i++)
                {
                    m_vxx[i].Date = m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - (101 - i)].Date;
                    m_vxx[i].AdjClosePrice = m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - (101 - i)].AdjClosePrice;
                    m_vxx[i].PctChg = m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - (101 - i)].AdjClosePrice / m_vxxQuotesFromIB[m_vxxQuotesFromIB.Count - (101 - i) - 1].AdjClosePrice - 1;
                }
            }


            // 1. Get Historical Data, SPY is from 1993-01-29, which is 23 years data now, but let's use maximum 20 years of data. Going back to 50 years will be not that adaptive.
            //int lookbackWindowSize = 21 * 260;  // we ask about 21 years, and we will cut it manually properly, to have exactly the same (20) January as February, as March, etc.
            //int nYearsInTrainingSet = 20;
            int nYearsInTrainingSet = uberVxxConfig.TotM_TrainingSetnYears;   // 25 temporarily, so that we can calculate the same as QuickTester for the longest history. Later we may go back to 20 years only.
            int lookbackWindowSize = (nYearsInTrainingSet + 1) * 260;  // we ask about 21 years, and we will cut it manually properly, to have exactly the same (20) January as February, as March, etc.
            m_spyQuotesFromSqlDB = SqlTools.LoadHistoricalQuotesAsync(new[] {
                    new QuoteRequest { Ticker = uberVxxConfig.TotM_TrainingSetTicker, nQuotes = lookbackWindowSize }}, DbCommon.AssetType.Stock).Result.
                    Select(row => new QuoteData { Date = (DateTime)row[1], AdjClosePrice = (double)Convert.ToDecimal(row[2]) }).OrderBy(row => row.Date).ToList(); // stocks come as double objects: (double)row[2], indexes as floats  (double)(float)row[2]

            // log the last 3 values (for later debugging)
            Utils.Logger.Trace($"{m_spyQuotesFromSqlDB[m_spyQuotesFromSqlDB.Count - 3].Date.ToString("yyyy-MM-dd")}: {m_spyQuotesFromSqlDB[m_spyQuotesFromSqlDB.Count - 3].AdjClosePrice}");
            Utils.Logger.Trace($"{m_spyQuotesFromSqlDB[m_spyQuotesFromSqlDB.Count - 2].Date.ToString("yyyy-MM-dd")}: {m_spyQuotesFromSqlDB[m_spyQuotesFromSqlDB.Count - 2].AdjClosePrice}");
            Utils.Logger.Trace($"{m_spyQuotesFromSqlDB[m_spyQuotesFromSqlDB.Count - 1].Date.ToString("yyyy-MM-dd")}: {m_spyQuotesFromSqlDB[m_spyQuotesFromSqlDB.Count - 1].AdjClosePrice}");

            // 2. Do some preprocess
            DateTime spyStartDate = DateTime.UtcNow.Date.AddYears(-1 * nYearsInTrainingSet);
            int nUsedSpySamples = m_spyQuotesFromSqlDB.Count - m_spyQuotesFromSqlDB.FindIndex(r => r.Date.Date >= spyStartDate.Date);
            int nPChg = nUsedSpySamples - 1;
            m_spy = new UberVxxQuoteData[nPChg];
            for (int i = nPChg - 1, j = m_spyQuotesFromSqlDB.Count - 1; i >= 0; i--, j--)
            {
                m_spy[i].Date = m_spyQuotesFromSqlDB[j].Date;
                m_spy[i].AdjClosePrice = m_spyQuotesFromSqlDB[j].AdjClosePrice;
                m_spy[i].PctChg = m_spyQuotesFromSqlDB[j].AdjClosePrice / m_spyQuotesFromSqlDB[j - 1].AdjClosePrice - 1.0;
            }

            // do we want to add the today's SPY real-time price? Not, because in the TotM model we don't use today data. This model depends on the monthly seasonality. So, only Dates matters.
            

            return isOkGettingHistoricalData;
        }

        


        public double GetForecastVxx()
        {
            double forecast = 0;    // temporary there is a preference hierarchy of Fomc/Holidays/TotM/Connor, but later we want a fairer combination

            double? uberVxxFomcHolidaysForecast = GetUberVxx_FomcAndHolidays_ForecastVxx();
            if (uberVxxFomcHolidaysForecast != null)
                forecast = (double)uberVxxFomcHolidaysForecast;
            else
            {
                double? uberVxxTotMForecast = GetUberVxx_TotM_TotMM_Summer_Winter_ForecastVxx();
                if (uberVxxTotMForecast != null)
                    forecast = (double)uberVxxTotMForecast;
                else
                {
                    double? connorForecast = GetConnorForecast();   // if UberVXX doesn't give forecast, use Connor
                    if (connorForecast != null)
                        forecast = (double)connorForecast;
                }
            }

            return forecast;       // 0 = neutral forecast, CASH
        }

    
    }
}
