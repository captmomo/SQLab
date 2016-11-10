﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace SQLab.Controllers.QuickTester.Strategies
{
    public class TAA
    {
        public static async Task<string> GenerateQuickTesterResponse(GeneralStrategyParameters p_generalParams, string p_strategyName, string p_params)
        {
            Stopwatch stopWatchTotalResponse = Stopwatch.StartNew();

            if (p_strategyName != "TAA")
                return null;

            //string strategyParams = p_params;
            string strategyParams = "SpyMinPctMove=0.01&VxxMinPctMove=0.01&LongOrShortTrade=Cash";
            int ind = -1;

            string spyMinPctMoveStr = null;
            if (strategyParams.StartsWith("SpyMinPctMove=", StringComparison.CurrentCultureIgnoreCase))
            {
                strategyParams = strategyParams.Substring("SpyMinPctMove=".Length);
                ind = strategyParams.IndexOf('&');
                if (ind == -1)
                {
                    ind = strategyParams.Length;
                }
                spyMinPctMoveStr = strategyParams.Substring(0, ind);
                if (ind < strategyParams.Length)
                    strategyParams = strategyParams.Substring(ind + 1);
                else
                    strategyParams = "";
            }
            string vxxMinPctMoveStr = null;
            if (strategyParams.StartsWith("VxxMinPctMove=", StringComparison.CurrentCultureIgnoreCase))
            {
                strategyParams = strategyParams.Substring("VxxMinPctMove=".Length);
                ind = strategyParams.IndexOf('&');
                if (ind == -1)
                {
                    ind = strategyParams.Length;
                }
                vxxMinPctMoveStr = strategyParams.Substring(0, ind);
                if (ind < strategyParams.Length)
                    strategyParams = strategyParams.Substring(ind + 1);
                else
                    strategyParams = "";
            }
            string longOrShortTrade = null;
            if (strategyParams.StartsWith("LongOrShortTrade=", StringComparison.CurrentCultureIgnoreCase))
            {
                strategyParams = strategyParams.Substring("LongOrShortTrade=".Length);
                ind = strategyParams.IndexOf('&');
                if (ind == -1)
                {
                    ind = strategyParams.Length;
                }
                longOrShortTrade = strategyParams.Substring(0, ind);
                if (ind < strategyParams.Length)
                    strategyParams = strategyParams.Substring(ind + 1);
                else
                    strategyParams = "";
            }

            double spyMinPctMove;
            bool isParseSuccess = Double.TryParse(spyMinPctMoveStr, out spyMinPctMove);
            if (!isParseSuccess)
            {
                throw new Exception("Error: spyMinPctMoveStr as " + spyMinPctMoveStr + " cannot be converted to number.");
            }

            double vxxMinPctMove;
            isParseSuccess = Double.TryParse(vxxMinPctMoveStr, out vxxMinPctMove);
            if (!isParseSuccess)
            {
                throw new Exception("Error: vxxMinPctMoveStr as " + vxxMinPctMoveStr + " cannot be converted to number.");
            }


            Stopwatch stopWatch = Stopwatch.StartNew();
            var getAllQuotesTask = StrategiesCommon.GetHistoricalAndRealtimesQuotesAsync(p_generalParams, (new string[] { "VXX", "SPY" }).ToList());
            var getAllQuotesData = await getAllQuotesTask;
            stopWatch.Stop();

            var vxxQoutes = getAllQuotesData.Item1[0];
            var spyQoutes = getAllQuotesData.Item1[1];

            string noteToUserCheckData = "", noteToUserBacktest = "", debugMessage = "", errorMessage = "";
            List<DailyData> pv = StrategiesCommon.DetermineBacktestPeriodCheckDataCorrectness(vxxQoutes, spyQoutes, "VXX", "SPY", ref noteToUserCheckData);

            DoBacktestInTheTimeInterval_TAA(vxxQoutes, spyQoutes, spyMinPctMove, vxxMinPctMove, longOrShortTrade, pv, ref noteToUserBacktest);

            stopWatchTotalResponse.Stop();
            StrategyResult strategyResult = StrategiesCommon.CreateStrategyResultFromPV(pv,
               noteToUserCheckData + "***" + noteToUserBacktest, errorMessage,
               debugMessage + String.Format("SQL query time: {0:000}ms", getAllQuotesData.Item2.TotalMilliseconds) + String.Format(", RT query time: {0:000}ms", getAllQuotesData.Item3.TotalMilliseconds) + String.Format(", All query time: {0:000}ms", stopWatch.Elapsed.TotalMilliseconds) + String.Format(", TotalC#Response: {0:000}ms", stopWatchTotalResponse.Elapsed.TotalMilliseconds));
            string jsonReturn = JsonConvert.SerializeObject(strategyResult);
            return jsonReturn;
        }


        private static void DoBacktestInTheTimeInterval_TAA(List<DailyData> vxxQoutes, List<DailyData> spyQoutes, double spyMinPctMove, double vxxMinPctMove, string longOrShortTrade, List<DailyData> pv, ref string noteToUserBacktest)
        {
            // temporary copy from private static void DoBacktestInTheTimeInterval_VXX_SPY_Controversial()

            bool? isTradeLongVXX = null;        // it means Cash
            if (String.Equals(longOrShortTrade, "Long"))
                isTradeLongVXX = true;
            else if (String.Equals(longOrShortTrade, "Short"))
                isTradeLongVXX = false;

            DateTime pvStartDate = pv[0].Date;
            DateTime pvEndDate = pv[pv.Count() - 1].Date;

            int iSpy = spyQoutes.FindIndex(row => row.Date == pvStartDate);
            int iVXX = vxxQoutes.FindIndex(row => row.Date == pvStartDate);


            double pvDaily = 100.0;
            pv[0].ClosePrice = pvDaily; // on the date when the quotes available: At the end of the first day, PV will be 1.0, because we trade at Market Close

            // on first day: short VXX, we cannot check what was 'yesterday' %change
            //pv[1].ClosePrice = pvDaily;
            double vxxChgOnFirstDay = vxxQoutes[iVXX + 1].ClosePrice / vxxQoutes[iVXX].ClosePrice - 1.0;
            double newNAVOnFirstDay = 2 * pvDaily - (vxxChgOnFirstDay + 1.0) * pvDaily;     // 2 * pvDaily is the cash
            pvDaily = newNAVOnFirstDay;
            pv[1].ClosePrice = pvDaily;

            int nControversialDays = 0;

            for (int i = 2; i < pv.Count(); i++)
            {
                double vxxChgYesterday = vxxQoutes[iVXX + i - 1].ClosePrice / vxxQoutes[iVXX + i - 2].ClosePrice - 1.0;
                double spyChgYesterday = spyQoutes[iSpy + i - 1].ClosePrice / spyQoutes[iSpy + i - 2].ClosePrice - 1.0;

                double vxxChg = vxxQoutes[iVXX + i].ClosePrice / vxxQoutes[iVXX + i - 1].ClosePrice - 1.0;
                if (Math.Sign(vxxChgYesterday) == Math.Sign(spyChgYesterday) && Math.Abs(spyChgYesterday) > (spyMinPctMove / 100.0) && Math.Abs(vxxChgYesterday) > (vxxMinPctMove / 100.0))       // Controversy, if they have the same sign, because usually they have the opposite sign
                {
                    nControversialDays++;
                    if (isTradeLongVXX == true)
                        pvDaily = pvDaily * (1.0 + vxxChg);
                    else if (isTradeLongVXX == false)
                    {
                        double newNAV = 2 * pvDaily - (vxxChg + 1.0) * pvDaily;     // 2 * pvDaily is the cash
                        pvDaily = newNAV;
                    }
                    // else we was in Cash today, so pvDaily = pvDaily;
                }
                else
                {// if no signal, short VXX with daily rebalancing
                    double newNAV = 2 * pvDaily - (vxxChg + 1.0) * pvDaily;     // 2 * pvDaily is the cash
                    pvDaily = newNAV;
                }

                pv[i].ClosePrice = pvDaily;
            }

            noteToUserBacktest = String.Format("{0:0.00%} of trading days are controversial days", (double)nControversialDays / (double)pv.Count());
        }   //DoBacktestInTheTimeInterval_VXX_SPY_Controversial()





    }   // class
}
