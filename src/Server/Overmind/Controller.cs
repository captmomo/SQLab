using DbCommon;
using SqCommon;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;     // this is the only timer available under DotNetCore
using System.Threading.Tasks;

namespace Overmind
{

    public class DailyData
    {
        public DateTime Date { get; set; }
        public double AdjClosePrice { get; set; }
    }

    // send Calendar emails (about Birthday (orsi), Pay-Rent, Accountant), 
    // it can do other things like: watch BIDU price and send SMS if it fell more than 5%;
    class Controller
    {
        static public Controller g_controller = new Controller();

        ManualResetEventSlim gMainThreadExitsResetEvent = null;

        //Your timer object goes out of scope and gets erased by Garbage Collector after some time, which stops callbacks from firing. Save reference to it in a member of class.
        long m_nHeartbeat = 0;
        Timer m_heartbeatTimer = null;
        Timer m_dailyMorningTimer = null;
        Timer m_dailyMiddayTimer = null;

        readonly DateTime g_DailyMorningTimerTime = new DateTime(2000, 1, 1, 9, 05, 0);      // the date part is not used only the time part. Activate every day 9:05, London Time Zone
        readonly DateTime g_DailyMiddayTimerTime = new DateTime(2000, 1, 1, 16, 45, 0);      // the date part is not used only the time part, Activate every day: 16:45, London Time Zone
        const int cHeartbeatTimerFrequencyMinutes = 5;

        static string g_htmlEmailStart =
@"<!DOCTYPE html><html><head><style>
.sqNormalText {
    font-size: 125%;
}
.sqImportantOK {
    font-size: 140%;
    color: #11228B;
    font-weight: bold;
}
</style></head>
<body class=""sqNormalText"">";

        static string g_htmlEmailEnd =
@"</body></html>";

        internal void Start()
        {
            gMainThreadExitsResetEvent = new ManualResetEventSlim(false);
            ScheduleDailyTimers();
        }

        internal void Exit()
        {
            gMainThreadExitsResetEvent.Set();
        }

        internal void ScheduleDailyTimers()
        {
            try
            {
                Utils.Logger.Info("ScheduleDailyTimers() BEGIN");

                TimeSpan untilDailyTimer = GetNextDailyTimerIntervalMsec(g_DailyMorningTimerTime);
                m_dailyMorningTimer = new System.Threading.Timer(new TimerCallback(DailyMorningTimer_Elapsed), null, untilDailyTimer, TimeSpan.FromMilliseconds(-1.0));
                Utils.Logger.Info("m_dailyTimer is scheduled at " + (DateTime.UtcNow + untilDailyTimer).ToString("MM'-'dd H:mm:ss", CultureInfo.InvariantCulture));

                TimeSpan untilDailyMarketWatcherTimer = GetNextDailyTimerIntervalMsec(g_DailyMiddayTimerTime);
                m_dailyMiddayTimer = new System.Threading.Timer(new TimerCallback(DailyMiddayTimer_Elapsed), null, untilDailyMarketWatcherTimer, TimeSpan.FromMilliseconds(-1.0));
                Utils.Logger.Info("m_dailyMarketWatcherTimer is scheduled at " + (DateTime.UtcNow + untilDailyMarketWatcherTimer).ToString("MM'-'dd H:mm:ss", CultureInfo.InvariantCulture));

                m_heartbeatTimer = new System.Threading.Timer((e) =>    // Heartbeat log is useful to find out when VM was shut down, or when the App crashed
                {
                    Utils.Logger.Info(String.Format("**m_nHeartbeat: {0} (at every {1} minutes)", m_nHeartbeat, cHeartbeatTimerFrequencyMinutes));
                    m_nHeartbeat++;
                }, null, TimeSpan.FromMinutes(0.5), TimeSpan.FromMinutes(cHeartbeatTimerFrequencyMinutes));

            }
            catch (Exception e)
            {
                Utils.Logger.Info(e, "ScheduleDailyTimers() Exception.");
            }
            Utils.Logger.Info("ScheduleDailyTimers() END");
        }


        static TimeSpan GetNextDailyTimerIntervalMsec(DateTime p_targetDateTimeLT)  // LT: LondonTime
        {
            try
            {
                TimeZoneInfo utcZone = TimeZoneInfo.Utc;
                TimeZoneInfo londonZone = Utils.FindSystemTimeZoneById(TimeZoneId.London);
                DateTime nowLT = TimeZoneInfo.ConvertTime(DateTime.UtcNow, utcZone, londonZone);  // LT: London time
                //DateTime nowLT = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, londonZone);  // LT: London time
                DateTime proposedTimerStartLT = new DateTime(nowLT.Year, nowLT.Month, nowLT.Day, p_targetDateTimeLT.Hour, p_targetDateTimeLT.Minute, p_targetDateTimeLT.Second);
                double scheduleStartTimerInMsec = (proposedTimerStartLT - nowLT).TotalMilliseconds;
                if (scheduleStartTimerInMsec <= 1000)  // it can be negative if we are after 9:00
                {
                    DateTime tomorrowLT = nowLT.AddDays(1); // if today is the 31st of the month, the next day is not a simple addition
                    proposedTimerStartLT = new DateTime(tomorrowLT.Year, tomorrowLT.Month, tomorrowLT.Day, p_targetDateTimeLT.Hour, p_targetDateTimeLT.Minute, p_targetDateTimeLT.Second); // next day
                    scheduleStartTimerInMsec = (proposedTimerStartLT - nowLT).TotalMilliseconds;
                }
                return TimeSpan.FromMilliseconds(scheduleStartTimerInMsec);
            }
            catch (Exception e)
            {
                Utils.Logger.Error(e, "Error in GetNextDailyTimerIntervalMsec().");
            }

            return TimeSpan.FromHours(24);  // let it be the next 24 hour
        }

     


        public void DailyMorningTimer_Elapsed(object p_sender) // Timer is coming on a ThreadPool thread
        {
            try
            {
                Utils.Logger.Info("DailyMorningTimer_Elapsed() BEGIN");
                Console.WriteLine(DateTime.UtcNow.ToString("MM'-'dd H:mm:ss", CultureInfo.InvariantCulture) + " : DailyMorningTimer_Elapsed() BEGIN");

                if (m_dailyMorningTimer != null)
                {
                    TimeSpan ts = GetNextDailyTimerIntervalMsec(g_DailyMorningTimerTime);
                    m_dailyMorningTimer.Change(ts, TimeSpan.FromMilliseconds(-1.0));
                    Utils.Logger.Info("m_dailyMorningTimer is scheduled at " + (DateTime.UtcNow + ts).ToString("MM'-'dd H:mm:ss", CultureInfo.InvariantCulture));
                }

                CheckHealthMonitorAlive();

                DateTime utcToday = DateTime.UtcNow.Date;
                string todayDateStr = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string todayMonthAndDayStr = DateTime.UtcNow.ToString("MM-dd", CultureInfo.InvariantCulture);

                if (todayMonthAndDayStr == "10-05")        // Orsi's birthday
                {
                    new Email { ToAddresses = Utils.Configuration["EmailGyantal"], Subject = "OvermindServer: Orsi's birthday", Body = "Orsi's birthday is on 1976-10-09.", IsBodyHtml = false }.Send();
                }

                Utils.Logger.Info("DailyMorningTimer_Elapsed(): Checking first day of the month");
                if (DateTime.UtcNow.AddDays(0).Day == 1)
                {
                    // Balazs Lukucz asked me that never send salaries on 30th or 31st of previous month. 
                    // So I will report to Barbara only on 1st day of every month, and maybe they got salaries later. 
                    // And this has an advantage that as I don't send the holidays report earlier, if they forget to tell me their 'last minute' day-offs, it is not reported to Barbara too early.
                    // So less headache overall.
                    new Email { ToAddresses = Utils.Configuration["EmailGyantal"], Subject = "OvermindServer: send holidays, bank report to accountant", Body = "Send holidays, bank report to accountant. In 3 days, it is the 1st day of the month. ", IsBodyHtml = false }.Send();
                }

                //double? price = GetAmazonProductPrice("https://www.amazon.co.uk/Electronics-Sennheiser-Professional-blocking-gaming-headset-Black/dp/B00JQDOANK/");
                //if (price == null || price <= 150.0)
                //{
                //    new Email
                //    {
                //        ToAddresses = Utils.Configuration["EmailGyantal"],
                //        Subject = "OvermindServer: Amazon price warning.",
                //        Body = (price == null) ?
                //            $"GetAmazonProductPrice() couldn't obtain current price. Check log file.":
                //            $"Time to buy Sennheiser GAME ZERO now. Amazon price dropped from 199.99 to {price}. Go to https://www.amazon.co.uk/Electronics-Sennheiser-Professional-blocking-gaming-headset-Black/dp/B00JQDOANK/ and buy headset now. See '2016-05, Sennheiser buying.txt'.",
                //        IsBodyHtml = false
                //    }.Send();
                //}

            }
            catch (Exception e)
            {
                Utils.Logger.Error(e.Message);
                new Email { ToAddresses = Utils.Configuration["EmailGyantal"], Subject = "OvermindServer: Crash", Body = "Crash. Exception: " + e.Message + ", StackTrace " + e.StackTrace + ", ToString(): " + e.ToString(), IsBodyHtml = false }.Send();
            }

            Utils.Logger.Info("DailyMorningTimer_Elapsed() END");
        }

        public void DailyMiddayTimer_Elapsed(object p_sender) // Timer is coming on a ThreadPool thread
        {
            Utils.Logger.Info("DailyMiddayTimer_Elapsed() BEGIN");
            Console.WriteLine(DateTime.UtcNow.ToString("MM'-'dd H:mm:ss", CultureInfo.InvariantCulture) + " : DailyMiddayTimer_Elapsed() BEGIN");
            try
            {
                if (m_dailyMiddayTimer != null)
                {
                    TimeSpan ts = GetNextDailyTimerIntervalMsec(g_DailyMiddayTimerTime);
                    m_dailyMiddayTimer.Change(ts, TimeSpan.FromMilliseconds(-1.0));
                    Utils.Logger.Info("m_dailyMarketWatcherTimer is scheduled at " + (DateTime.UtcNow + ts).ToString("MM'-'dd H:mm:ss", CultureInfo.InvariantCulture));
                }

                CheckHealthMonitorAlive();

                // TODO: if market holiday: it shouldn't process anything either
                if (DateTime.UtcNow.DayOfWeek == DayOfWeek.Saturday || DateTime.UtcNow.DayOfWeek == DayOfWeek.Sunday)
                {
                    Utils.Logger.Debug("DailyMiddayTimer_Elapsed(). Weekend is detected. Don't do a thing.");
                    return;
                }

                CheckIfTomorrowIsMonthlyOptionExpirationDay();
                CheckIntradayStockPctChanges();
                CheckLastClosePrices();

                Utils.Logger.Info("DailyMiddayTimer_Elapsed()-4");
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception in DailyMiddayTimer_Elapsed(). See log file.");
                Utils.Logger.Error(e, "Exception in DailyMiddayTimer_Elapsed()");
                new Email { ToAddresses = Utils.Configuration["EmailGyantal"], Subject = "OvermindServer: Crash", Body = "Crash. Exception: " + e.Message + ", StackTrace " + e.StackTrace + ", ToString(): " + e.ToString(), IsBodyHtml = false }.Send();
            }
            Utils.Logger.Info("DailyMiddayTimer_Elapsed() END");
        }

        private static void CheckHealthMonitorAlive()
        {
            string receivedTcpMsg = "";
            try
            {
                using (var client = new TcpClient())
                {
                    Task task = client.ConnectAsync(ServerIp.HealthMonitorPublicIp, HealthMonitorMessage.DefaultHealthMonitorServerPort);
                    if (Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))).Result != task)
                    {
                        Utils.Logger.Error("CheckHealthMonitorAlive(): Error:HealthMonitor server: client.Connect() timeout.");
                    }

                    BinaryWriter bw = new BinaryWriter(client.GetStream());
                    bw.Write((Int32)HealthMonitorMessageID.Ping);
                    bw.Write("");
                    bw.Write((Int32)HealthMonitorMessageResponseFormat.String);

                    BinaryReader br = new BinaryReader(client.GetStream());
                    receivedTcpMsg = br.ReadString();
                }

                Utils.Logger.Debug("ReportHealthMonitorCurrentStateToDashboardInJSON() after WaitMessageFromWebJob()");
            }
            catch (Exception e)
            {
                Utils.Logger.Error("Error:HealthMonitor SendMessage exception:  " + e);       
            }

            Utils.Logger.Debug("CheckHealthMonitorAlive() returned answer: " + receivedTcpMsg);
            Console.WriteLine($"HealthMonitor Ping return: '{receivedTcpMsg}'");

            bool isHealthMonitorAlive = receivedTcpMsg.StartsWith("Ping. Healthmonitor UtcNow: ");
            if (isHealthMonitorAlive)
                return;

            new Email
            {
                ToAddresses = Utils.Configuration["EmailGyantal"],
                Subject = "OvermindServer Warning! : HealthMonitor is NOT Alive.",
                Body = $"OvermindServer Warning! : HealthMonitor is NOT Alive.",
                IsBodyHtml = false
            }.SendAsync().FireParallelAndForgetAndLogErrorTask();
        }

        private static void CheckIntradayStockPctChanges()
        {
            string gyantalEmailInnerlStr = String.Empty;
            string gyantalPhoneCallInnerStr = String.Empty;
            string charmatEmailInnerlStr = String.Empty;
            string charmatPhoneCallInnerStr = String.Empty;

            double kwebTodayPctChange = GetTodayPctChange("KWEB");
            if (Math.Abs(kwebTodayPctChange) >= 0.04)
            {
                gyantalEmailInnerlStr += "KWEB price warning: bigger than usual move. In percentage: " + (kwebTodayPctChange * 100).ToString("0.00") + @"%." + Environment.NewLine;
                gyantalPhoneCallInnerStr += "the ticker K W E B, ";
            }

            double vxxTodayPctChange = GetTodayPctChange("VXX");
            if (Math.Abs(vxxTodayPctChange) >= 0.06)
            {
                gyantalEmailInnerlStr += "VXX price warning: bigger than usual move. In percentage: " + (vxxTodayPctChange * 100).ToString("0.00") + @"%";
                gyantalPhoneCallInnerStr += "the ticker V X X ";
                charmatEmailInnerlStr += "VXX price warning: bigger than usual move. In percentage: " + (vxxTodayPctChange * 100).ToString("0.00") + @"%";
                charmatPhoneCallInnerStr += "the ticker V X X ";
            }

            double amznTodayPctChange = GetTodayPctChange("AMZN");
            if (Math.Abs(amznTodayPctChange) >= 0.04)
            {
                charmatEmailInnerlStr += "Amazon price warning: bigger than usual move. In percentage: " + (amznTodayPctChange * 100).ToString("0.00") + @"%." + Environment.NewLine;
                charmatPhoneCallInnerStr += "the ticker Amazon, ";
            }

            double googleTodayPctChange = GetTodayPctChange("FB");
            if (Math.Abs(googleTodayPctChange) >= 0.04)
            {
                charmatEmailInnerlStr += "Google price warning: bigger than usual move. In percentage: " + (googleTodayPctChange * 100).ToString("0.00") + @"%." + Environment.NewLine;
                charmatPhoneCallInnerStr += "the ticker Google, ";
            }

            if (!String.IsNullOrEmpty(gyantalEmailInnerlStr))
            {
                new Email { ToAddresses = Utils.Configuration["EmailGyantal"], Subject = "Overmind: SnifferQuant Price Warning", Body = gyantalEmailInnerlStr, IsBodyHtml = false }.Send();
                var call = new PhoneCall
                {
                    FromNumber = Caller.Gyantal,
                    ToNumber = PhoneCall.PhoneNumbers[Caller.Gyantal],
                    Message = "This is a warning notification from SnifferQuant. There's a large up or down movement in " + gyantalPhoneCallInnerStr + " ... I repeat " + gyantalPhoneCallInnerStr,
                    NRepeatAll = 2
                };
                Console.WriteLine("call.MakeTheCall() return: " + call.MakeTheCall());
            }

            if (!String.IsNullOrEmpty(charmatEmailInnerlStr))
            {
                new Email { ToAddresses = Utils.Configuration["EmailCharmat0"], Subject = "SnifferQuant Price Warning", Body = charmatEmailInnerlStr, IsBodyHtml = false }.Send();
                var call = new PhoneCall
                {
                    FromNumber = Caller.Gyantal,
                    ToNumber = PhoneCall.PhoneNumbers[Caller.Charmat0],
                    Message = "This is a warning notification from SnifferQuant. There's a large up or down movement in " + charmatPhoneCallInnerStr + " ... I repeat " + charmatPhoneCallInnerStr,
                    NRepeatAll = 2
                };
                Console.WriteLine("call.MakeTheCall() return: " + call.MakeTheCall());
            }
        }

        // 2017-11-03: YF discontinued it: http://finance.yahoo.com/d/quotes.csv?s=BIDU : "It has come to our attention that this service is being used in violation of the Yahoo Terms of Service. As such, the service is being discontinued. For all future markets and equities data research, please refer to finance.yahoo.com."
        //private static double GetTodayPctChange(string p_ticker)
        //{
        //    Utils.Logger.Trace("GetTodayPctChange(): " + p_ticker);
        //    //var biduDelayedPriceCSV = new HttpClient().GetStringAsync("http://download.finance.yahoo.com/d/quotes.csv?s=" + p_ticker + "&f=sl1d1t1c1ohgv&e=.csv").Result;
        //    var biduDelayedPriceCSV = new HttpClient().GetStringAsync("http://finance.yahoo.com/d/quotes.csv?s=" + p_ticker + "&f=sl1d1t1c1ohgv&e=.csv").Result;
        //    Utils.Logger.Trace("HttpClient().GetStringAsync returned: " + biduDelayedPriceCSV);
        //    string[] biduDelayedPriceSplit = biduDelayedPriceCSV.Split(new char[] { ',', ' ' });
        //    double realTimePrice = Double.Parse(biduDelayedPriceSplit[1]);
        //    double dailyChange = Double.Parse(biduDelayedPriceSplit[4]);
        //    double yesterdayClose = realTimePrice - dailyChange;
        //    double todayPercentChange = realTimePrice / yesterdayClose - 1;
        //    return todayPercentChange;
        //}

        // 2017-11-02: YF is discontinued (V7 API uses crumbs), GF uses cookies two (although it is fast, and it is real-time), decided to use CNBC for a while
        // We could do https://www.snifferquant.net/YahooFinanceForwarder?yffOutFormat=csv ..., but that depends on the web service and we don't want this Overmind to depend on a website
        private static double GetTodayPctChange(string p_exchangeWithTicker)    // for GoogleFinance: TSE:VXX is the Toronto stock exchange, we need "NYSEARCA:VXX"
        {
            Utils.Logger.Trace("GetTodayPctChange(): " + p_exchangeWithTicker);
            // https://finance.google.com/finance?q=BATS%3AVXX
            string url = $"https://www.cnbc.com/quotes/?symbol=" + p_exchangeWithTicker.Replace(":", "%3A");
            Utils.Logger.Trace("DownloadStringWithRetry() queried with:'" + url + "'");
            string priceHtml = String.Empty;
            Utils.DownloadStringWithRetry(out priceHtml, url, 5, TimeSpan.FromSeconds(5), true);        

            string firstCharsWithSubString = !String.IsNullOrWhiteSpace(priceHtml) && priceHtml.Length >= 300 ? priceHtml.Substring(0, 300) : priceHtml;
            Utils.Logger.Trace("HttpClient().GetStringAsync returned: " + firstCharsWithSubString);

            double? realTimePrice = null, dailyChange = null;
            int iLastPriceStart = priceHtml.IndexOf($"\"last\":\"");
            if (iLastPriceStart != -1)
            {
                iLastPriceStart += $"\"last\":\"".Length;
                int iLastPriceEnd = priceHtml.IndexOf("\"", iLastPriceStart);
                if (iLastPriceEnd != -1)
                {
                    var lastPriceStr = priceHtml.Substring(iLastPriceStart, iLastPriceEnd - iLastPriceStart);
                    realTimePrice = Double.Parse(lastPriceStr);

                    int iChangePriceStart = priceHtml.IndexOf($"\"change\":\"", iLastPriceEnd);
                    if (iChangePriceStart != -1)
                    {
                        iChangePriceStart += $"\"change\":\"".Length;
                        int iChangePriceEnd = priceHtml.IndexOf("\"", iChangePriceStart);
                        if (iChangePriceEnd != -1)
                        {
                            var changePriceStr = priceHtml.Substring(iChangePriceStart, iChangePriceEnd - iChangePriceStart);
                            dailyChange = Double.Parse(changePriceStr);

                            double yesterdayClose = (double)realTimePrice - (double)dailyChange;
                            double todayPercentChange = (double)realTimePrice / yesterdayClose - 1;
                            return todayPercentChange;
                        }
                    }
                }
            }

            //string[] biduDelayedPriceSplit = priceHtml.Split(new char[] { ',', ' ' });
            //realTimePrice = Double.Parse(biduDelayedPriceSplit[1]);
            //double dailyChange = Double.Parse(biduDelayedPriceSplit[4]);
            //double yesterdayClose = realTimePrice - dailyChange;
            //double todayPercentChange = realTimePrice / yesterdayClose - 1;
            //return todayPercentChange;
            return Double.NaN;
        }

        private static void CheckLastClosePrices()
        {
            // it is difficult with these data sources. Sometimes YF, sometimes GF is not good. We try to use our SQL then.
            DateTime endDateUtc = DateTime.UtcNow.AddDays(-1);
            DateTime startDateUtc = endDateUtc.AddDays(-90);   // we need the last 50 items, but ask more trading days. Just to be sure. With this 90 calendar days, we got 62 trading days in July. However, in the Xmas season, we get less. So, keep the 90 calendar days.

            List<string> tickers = new List<string>() { "^VIX" };
            ushort sqlReturnedColumns = QuoteRequest.TDC;       // QuoteRequest.All or QuoteRequest.TDOHLCVS
            var sqlReturnTask = SqlTools.GetHistQuotesAsync(startDateUtc, endDateUtc, tickers, sqlReturnedColumns);
            var sqlReturnData = sqlReturnTask.Result;
            var sqlReturn = sqlReturnData.Item1;

            List<List<DailyData>> returnQuotes = null;
            // sql query of "VXX.SQ" gives back tickers of VXX and also tickers of "VXX.SQ"
            int closePriceIndex = 2;

            returnQuotes = tickers.Select(ticker =>
            {
                IEnumerable<object[]> mergedRows = SqlTools.GetTickerAndBaseTickerRows(sqlReturn, ticker);
                return mergedRows.Select(
                    row => new DailyData()
                    {
                        Date = ((DateTime)row[1]),
                        AdjClosePrice = (double)Convert.ToDecimal(row[closePriceIndex])  // row[2] is object(double) if it is a stock (because Adjustment multiplier), and object(float) if it is Indices. However Convert.ToDouble(row[2]) would convert 16.66 to 16.6599999
                    }).ToList();
            }).ToList();


            // Check that 1.2*SMA(VIX, 50) < VIX_last_close:  (this is used by the VIX spikes document)
            // this is used in the Balazs's VIX spikes gDoc: https://docs.google.com/document/d/1YA8uBscP1WbxEFIHXDaqR50KIxLw9FBnD7qqCW1z794

            var quotes = returnQuotes[0];

            double lastClose = quotes[quotes.Count - 1].AdjClosePrice;
            int nSmaDays = 50;
            double sma = 0;
            for (int i = 0; i < nSmaDays; i++)
            {
                sma += quotes[quotes.Count - 1 - i].AdjClosePrice;
            }
            sma /= (double)nSmaDays;

            bool isVixSpike = 1.2 * sma < lastClose;
            // VIX spike can be detected with another formulation if wished
            //Maybe I should use this(or Both) in the VIX checking email service
            //"Marked on the chart are instances where the VIX has risen by at least 30% (from close to"
            //the highest high) in a five-day period when a previous 30 +% advance had not occurred in the prior ten trading days.There have been 70 such occurrences of these spikes in the above-mentioned time period.
            //> But Balazs showed it is not really useful.Still, it can be used. Read that article and email again.
            //See "2017-Forecasting Volatility Tsunamy, Balazs-gmail.pdf"

            if (isVixSpike)  // if  1.2*SMA(VIX, 50) < VIX_last_close, sends an email. So, we can trade VIX MR subjectively.
            {
                string subjectPart = "VIX spike detected";
                StringBuilder sb = new StringBuilder(g_htmlEmailStart);
                sb.Append(@"<span class=""sqImportantOK""><strong>VIX Spike</strong> is detected!</span><br/><br/>");
                sb.Append($"Using yesterday close prices for VIX, the condition<br/> <strong>'VIX_lastClose &gt; 1.2 * SMA(VIX, 50)'</strong><br/> ({lastClose:0.##} &gt;  1.2 * {sma:0.##}) was triggered.<br/>");
                sb.Append(@"Our <a href=""https://docs.google.com/document/d/1YA8uBscP1WbxEFIHXDaqR50KIxLw9FBnD7qqCW1z794"">VIX spikes collection gDoc</a> uses the same formula for identifying panic times.<br/>");
                sb.Append("Intraday price was not used for this trigger. You need to act with a delay anyway.<br/><br/>");
                sb.Append("<strong>Action: </strong><br/> This is a Mean Reversion (MR) opportunity.<br/> Trading 'fading the VIX spike' can be considered.<br/>");
                sb.Append("Maybe risking 1/10th of the portfolio.<br/> Doubling down in another chunk maximum 3 times.<br/>");
                sb.Append(g_htmlEmailEnd);

                string emailHtmlBody = sb.ToString();
                new Email { ToAddresses = Utils.Configuration["EmailGyantal"], Subject = "Overmind: " + subjectPart, Body = emailHtmlBody, IsBodyHtml = true }.Send();
                new Email { ToAddresses = Utils.Configuration["EmailCharmat0"], Subject = "SnifferQuant: " + subjectPart, Body = emailHtmlBody, IsBodyHtml = true }.Send();
                new Email { ToAddresses = Utils.Configuration["EmailBalazs"], Subject = "SnifferQuant: " + subjectPart, Body = emailHtmlBody, IsBodyHtml = true }.Send();
            }

        }

        private static void CheckIfTomorrowIsMonthlyOptionExpirationDay()
        {
            // Expiration date: USA: 3rd Friday of the month. When that Friday falls on a holiday, the expiration date is on the Thursday immediately before.
            DateTime tomorrowDateUtc = DateTime.UtcNow.AddDays(1);
            if (tomorrowDateUtc.DayOfWeek != DayOfWeek.Friday)
                return;
            // method 1: if it is Friday, subtract 7 days backwards 2 times and check if that is a positive number. However, subtracting another 7 days should be a negative number
            // method 2: think about possible day of the months.
            //          > If 1st day of the month is Friday, then the 3rd Friday is: 1+7+7=15th  (it happened in 2018-06), that is the earliest possible date.
            //          > if 1st day of the month Saturday, then the 1rd Friday is 7th. The 3rd Friday is 7+7+7=21th, that is the largest day possible for the 3rd Friday
            if (tomorrowDateUtc.Day < 15 || tomorrowDateUtc.Day > 21)
                return;
            // if we are here, it is Friday and dayOfMonth in [15,21], which is the 3rd Friday
            string subjectPart = "Monthly Option expiration. Trade HarryLong 2 manually!";
            StringBuilder sb = new StringBuilder(g_htmlEmailStart);
            sb.Append(
                @"<small>Because of the EU PRIIPs (KID) Regulation, IB UK doesn't allow US domiciled ETFs like SPY, QQQ, VXX to buy until the account is worth less than 500K EUR. Workaround is options.<br/>
- for shorting VXX stock, we buy VXX Put option (cheap, very close to expiration, when time value is tiny), then we let it expire or force-exercise. The result is 100 short VXX stock immediately. It WORKED !, because it is technically not a stock shorting/buying, and EU regulation protect Funds (ETF) only, and when you trade options you are assumed to be sophisticated investor already.<br/>
- only 100 stocks in batches can be obtained, but we can even go more granular by exercising 1 option for 100 shares, then liquidating 2/3rd of it instantly. Liquidation is allowed.<br/>
- Don't fret if it is rebalanced only after 2 months. Actually it (SR) is better.<br/>
&nbsp;&nbsp;&nbsp;&nbsp;Daily,1d: CAGR: 58.24%, SR 1.14<br/>&nbsp;&nbsp;&nbsp;&nbsp;Daily,20d: CAGR: 59.09%, SR 1.16<br/>&nbsp;&nbsp;&nbsp;&nbsp;Daily,40d: CAGR: 59.53%, SR 1.19 
</small><br/> 
<h2>ToDo:</h2>
<ul>
<li>VBroker: uncomment HarryLong scheduling, run HarryLong simulation locally, that calculates the proposed number of shares to trade. Comment out HarryLong scheduling again (so that it is not sent to the server later).</li>
<li>Round the number of stocks up to the nearest 100 or just ignore them if tiny. Buy options and force exercise them. Liquidate the unnecessary stock parts.</li>
<li>Register trades as stocks into SQDeskop.</li>
</ul>");
            sb.Append(g_htmlEmailEnd);

            string emailHtmlBody = sb.ToString();
            new Email { ToAddresses = Utils.Configuration["EmailGyantal"], Subject = "Overmind: " + subjectPart, Body = emailHtmlBody, IsBodyHtml = true }.Send();

        }

        // Amazon UK price history can be checked in uk.camelcamelcamel.com, for example: http://uk.camelcamelcamel.com/Sennheiser-Professional-blocking-gaming-headset-Black/product/B00JQDOANK
        private static double? GetAmazonProductPrice(string p_amazonProductUrl)
        {
            string errorMessage = String.Empty;
            double price = 0.0;
            var webpage = new HttpClient().GetStringAsync(p_amazonProductUrl).Result;
            Utils.Logger.Info("HttpClient().GetStringAsync returned: " + ((webpage.Length > 100) ? webpage.Substring(0, 100) : webpage));

            // <span id="priceblock_ourprice" class="a-size-medium a-color-price">£199.95</span>
            string searchStr = @"id=""priceblock_ourprice"" class=""a-size-medium a-color-price"">";
            int startInd = webpage.IndexOf(searchStr);
            if (startInd == -1)
            {   // it is expected (not an exception), that sometimes Amazon changes its website, so we will fail. User will be notified.
                Utils.Logger.Info($"searchString '{searchStr}' was not found.");
                return null;
            }
            int endInd = webpage.IndexOf('<', startInd + searchStr.Length);
            if (endInd == -1)
            {   // it is expected (not an exception), that sometimes Amazon changes its website, so we will fail. User will be notified.
                Utils.Logger.Info($"'<' after searchString '{searchStr}' was not found.");
                return null;
            }
            string priceStr = webpage.Substring(startInd + searchStr.Length + 1, endInd - (startInd + searchStr.Length + 1));
            if (!Double.TryParse(priceStr, out price))
            {
                Utils.Logger.Info($"{priceStr} cannot be parsed to Double.");
                return null;
            }
            return price;
        }

        internal void TestSendingEmailAndPhoneCall()
        {
            Console.WriteLine("TestSendingEmail started.");
            Utils.Logger.Info("TestSendingEmail() START");
            DailyMorningTimer_Elapsed(null);

            double todayPercentChange = GetTodayPctChange("BIDU");
            Console.WriteLine("BIDU %change: " + (todayPercentChange * 100).ToString("0.00") + @"%");
            Utils.Logger.Info("BIDU %change: " + (todayPercentChange * 100).ToString("0.00") + @"%");
            //if (Math.Abs(todayPercentChange) >= 0.04)
            if (Math.Abs(todayPercentChange) >= 0.00)
            {
                new Email { ToAddresses = Utils.Configuration["EmailGyantal"], Subject = "OvermindServer: BIDU price % move", Body = "BIDU price % move. In percentage: " + (todayPercentChange * 100).ToString("0.00") + @"%", IsBodyHtml = false }.SendAsync().FireParallelAndForgetAndLogErrorTask();

                Console.WriteLine("Email was sent.");
                Utils.Logger.Info("Email was sent.");

                var call = new PhoneCall
                {
                    FromNumber = Caller.Gyantal,
                    ToNumber = PhoneCall.PhoneNumbers[Caller.Gyantal],
                    Message = "This is a warning notification from SnifferQuant. There's a large up or down movement in the ticker B I D U. ... I repeat the ticker: B I D U.",
                    NRepeatAll = 2
                };
                // skipped temporarily
                bool phoneCallSuccess = call.MakeTheCall();
                Console.WriteLine("Phonecall success: " + phoneCallSuccess);
                Utils.Logger.Info("Phonecall success: " + phoneCallSuccess);
            }

            Console.WriteLine("TestSendingEmail Finished.");
            Utils.Logger.Info("TestSendingEmail() END");
        }

        internal void TestCheckingAmazonPrice()
        {
            Utils.Logger.Info("TestCheckingAmazonPrice() START");
            double? price = GetAmazonProductPrice("https://www.amazon.co.uk/Electronics-Sennheiser-Professional-blocking-gaming-headset-Black/dp/B00JQDOANK/");
            string priceStr = (price == null) ? "null" : price.ToString();
            Console.WriteLine($"Amazon price is: {priceStr}. Sending email.");

            new Email
            {
                ToAddresses = Utils.Configuration["EmailGyantal"],
                Subject = "OvermindServer: Amazon price warning: Time to buy Sennheiser GAME ZERO now",
                Body = $"Time to buy Sennheiser GAME ZERO now. Amazon price dropped from 199.99 to {priceStr}. Go to https://www.amazon.co.uk/Electronics-Sennheiser-Professional-blocking-gaming-headset-Black/dp/B00JQDOANK/ and buy headset now. See '2016-05, Sennheiser buying.txt'. ",
                IsBodyHtml = false
            }.SendAsync().FireParallelAndForgetAndLogErrorTask();

        }
    }
}
