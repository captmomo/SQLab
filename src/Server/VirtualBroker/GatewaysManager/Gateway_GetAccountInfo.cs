﻿using DbCommon;
using SqCommon;
using IBApi;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Utils = SqCommon.Utils;
using System.Text;
using System.Diagnostics;

namespace VirtualBroker
{
  

    public partial class Gateway
    {
        List<AccSum> m_accSums;
        List<AccPos> m_accPoss;
        string[] m_exclSymbolsArr;
        private readonly object m_getAccountSummaryLock = new object();        

        public void AccSumArrived(int p_reqId, string p_tag, string p_value, string p_currency)
        {
            m_accSums.Add(new AccSum() { Tag = p_tag, Value = p_value, Currency = p_currency });
        }

        ManualResetEventSlim m_getAccountSummaryMres;
        public void AccSumEnd(int p_reqId)
        {
            if (m_getAccountSummaryMres != null)
                m_getAccountSummaryMres.Set();  // Sets the state of the event to signaled, which allows one or more threads waiting on the event to proceed.

            // if you don't cancel it, all the data update come every 1 minute, which might be good, because we can give it to user instantenously....
            // However, it would be an unnecessary traffic all the time... So, better to Cancel the data streaming.
            BrokerWrapper.CancelAccountSummary(p_reqId);
        }

        private readonly object m_getAccountPositionsLock = new object();
        public void AccPosArrived(string p_account, Contract p_contract, double p_pos, double p_avgCost)
        {
            // 2018-11: EUR cash is coming ONLY on DeBlanzac account, not Main account, neither Agy, which also has many other currencies. Maybe it is only a 'virtual' cash FX position. Assume it is virtual, so ignore it.
            if (p_contract.SecType == "CASH")
                return;
            if (p_pos != 0.0 && !m_exclSymbolsArr.Contains(p_contract.Symbol))   // If a position is 0, it means we just sold it, but IB reports it during that day, because of Realized P&L. However, we don't need that position any more.
                m_accPoss.Add(new AccPos() { Contract = p_contract, Position = p_pos, AvgCost = p_avgCost });
        }

        ManualResetEventSlim m_getAccountPosMres;
        public void AccPosEnd()
        {
            if (m_getAccountPosMres != null)
                m_getAccountPosMres.Set();  // Sets the state of the event to signaled, which allows one or more threads waiting on the event to proceed.
        }

        public bool GetAccountSums(List<AccSum> p_accSums)
        {
            m_accSums = p_accSums;

            int accReqId = -1;
            try
            {
                Stopwatch sw1 = Stopwatch.StartNew();
                lock (m_getAccountSummaryLock)          // IB only allows one query at a time, so next client has to wait
                {
                    if (m_getAccountSummaryMres == null)
                        m_getAccountSummaryMres = new ManualResetEventSlim(false);  // initialize as unsignaled
                    else
                        m_getAccountSummaryMres.Reset();        // set to unsignaled, which makes thread to block

                    accReqId = BrokerWrapper.ReqAccountSummary();

                    bool wasLightSet = m_getAccountSummaryMres.Wait(5000);     // timeout at 5sec
                    if (!wasLightSet)
                        Utils.Logger.Error("ReqAccountSummary() ended with timeout error.");
                    //m_getAccountSummaryMres.Dispose();    // not necessary. We keep it for the next sessions for faster execution.
                }
                sw1.Stop();
                Console.WriteLine($"ReqAccountSummary() ends in {sw1.ElapsedMilliseconds}ms GW user: '{this.GatewayUser}', Thread Id= {Thread.CurrentThread.ManagedThreadId}");
            }
            catch (Exception e)
            {
                Utils.Logger.Error("ReqAccountSummary() ended with exception: " + e.Message);
                return false;
            }
            finally
            {
                if (accReqId != -1)
                    BrokerWrapper.CancelAccountSummary(accReqId);
            }
            return true;
        }

        public bool GetAccountPoss(List<AccPos> p_accPos, string[] p_exclSymbolsArr)
        {
            m_accPoss = p_accPos;
            m_exclSymbolsArr = p_exclSymbolsArr;
            try
            {
                Stopwatch sw2 = Stopwatch.StartNew();
                lock (m_getAccountPositionsLock)          //ReqPositions() doesn't have a reqID, so if we allow multiple threads to do it at the same time, we cannot sort out the output
                {
                    if (m_getAccountPosMres == null)
                        m_getAccountPosMres = new ManualResetEventSlim(false);  // initialize as unsignaled
                    else
                        m_getAccountPosMres.Reset();        // set to unsignaled, which makes the thread to block

                    BrokerWrapper.ReqPositions();
                    bool wasLightSet = m_getAccountPosMres.Wait(5000);     // timeout at 5sec
                    if (!wasLightSet)
                        Utils.Logger.Error("ReqPositions() ended with timeout error.");
                }
                sw2.Stop();
                Console.WriteLine($"ReqPositions() ends in {sw2.ElapsedMilliseconds}ms GW user: '{this.GatewayUser}', Thread Id= {Thread.CurrentThread.ManagedThreadId}");
            }
            catch (Exception e)
            {
                Utils.Logger.Error("ReqPositions() ended with exception: " + e.Message);
            }
            return true;
        }

        
    }

    }
