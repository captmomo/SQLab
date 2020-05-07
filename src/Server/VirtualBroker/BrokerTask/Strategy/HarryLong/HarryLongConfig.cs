using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VirtualBroker
{
    public class HarryLongConfig
    {
        // DWT is used only if short UWT is not available, DWT can be left here, no harm, UNG is used if UWT cannot be traded by user
        // SCO is used instead of UWT sometimes
        public string[] AllPotentialTickersOfPortfolios = new string[] { "SVXY", "VXX", "ZIV", "TQQQ", "TMV", "UCO", "UGAZ",       "SCO", "UNG",  "VXZ", "TMF" };  

        public string[] DefaultTradedTickers = new string[] { "TVIX", "TMV" };            // strategy default is used only if PortfolioParamHarryLong is not given
        public double[] DefaultTradedAssetsWeights = new double[] { -0.35, -0.65 };       // strategy default is used only if PortfolioParamHarryLong is not given

        //public int RebalancingTradingDays = 1;  // 1 or 5 or 10 days. It is not really used, as we rebalance every day.
    }

    // Portfolio specific parameters are here. User1's portfolio1 may use double leverage than User2's portfolio2. The Common Strategy params should go to StrategyConfig.cs
    public class PortfolioParamHarryLong : IPortfolioParam // default values here for default users
    {
        // based on $Volume, UVXY is about 3.5x more traded than TVIX. We started with UVXY. But 2017-02-14: switched to TVIX, because of forced "SHORT STOCK POSITION BOUGHT IN" without advance notice. UVXY was not available for shorting for 5 days, and borrowing cost was 20%, while it is 3.4% for TVIX.
        // TMV is 3x TLT: Direxion Daily 20+ Yr Trsy Bear 3X ETF (TMV)
        //public string[] Tickers = new string[] { "TVIX", "TMV", "UWT", "UGAZ" };      // portfolio defaults

        //- HarryLong changing Short TVIX to long 2x SVXY.
        //QuickTester shows the expected performance changes if we replace TVIX with 2x SVXY.
        //>short TVIX: 35% ? no idea, buggy data, but it can be estimated by shorting UVXY:
        //>Start Date: 2011-10-04
        //> -35% short UVXY: CAGR: 105.88%	Annualized StDev: 50.02%	Sharpe: 2.12, (this is almost like TVIX), however -6% shorting borrowing fee = 99% CAGR.This was played until 2017-12-12. However, TVIX requires margin of 4x.So, if you play 10K short TVIX, the margin required is 40K.
        //> -70% short VXX: CAGR: 100.01%	Annualized StDev: 50.05%	Sharpe: 2   this is the synthetic one, if played by short VXX.However, short VXX requires margin of x2.3
        //> 70% long XIV: CAGR: 87.29%	Annualized StDev: 50.17%	Sharpe: 1.74
        //> 70% long SVXY: CAGR: 87.18%	Annualized StDev: 50.3%	Sharpe: 1.73.  I will use this SVXY.Don't touch the XIV position which is long term. It requiers margin of x1.0
        //So, should we change TVIX to 2x SVXY ? No, because perfarmance is decreased from 99% to 87% CAGR.
        //>-However, I have no other option.There are IB Margin problems.
        //>-And this way, it would be safer if VIX triples in 1 day.
        //>-Also forced liquidation risk is eliminated.
        //>-Also missing trades are eliminated when there is no TVIX available for shorting.
        //>-In brief, this is the less performance, but safer trade.Let's do SVXY.
        //public string[] Tickers = new string[] { "SVXY", "TMV", "UWT", "UGAZ" };      // portfolio defaults until 2018-02-21
        //public double[] AssetsWeights = new double[] { 0.70, -0.71, -0.07, -0.22 }; // portfolio defaults until 2018-02-21, Markowitz MPT optimal weight using 135% allocation. With daily rebalancing it is safe.
        public string[] Tickers = new string[] { "SVXY", "VXX", "ZIV", "TQQQ", "TMV", "UCO", "UGAZ" };      // portfolio defaults after 2018-02-21
        //public double[] AssetsWeights = new double[] { 0.15, -0.05, 0.10, 0.20, -0.85, -0.09, -0.26 }; // portfolio defaults after 2018-02-21
        public double[] AssetsWeights = new double[] { 0.15, -0.05, 0.10, 0.25, -0.85, -0.135, -0.26 }; // portfolio defaults after 2018-03-01. SVXY.Classid (1x) deleveraged to SVXY.Light(0.5). We compensate it with +5% more TQQQ. See gDoc chapter.
    }
}
