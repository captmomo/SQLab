using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DbCommon
{
    public class DbPortfolio
    {
        public string Name { get; set; }
        public HQUserID HQUserID { get; set; }

        public int PortfolioID { get; set; }
        public List<PortfolioPosition> TodayPositions { get; set; }
        //public List<Transaction> HistoricalTransactions { get; set; }   // Historical Transactions don't really need historical PiPs. Maybe only for debug Purposes
    }

    // for SQL INSERTS
    public class DbTransaction
    {
        public int PortfolioID { get; set; } 
        public TransactionType TransactionType { get; set; }
        public AssetType AssetTypeID { get; set; }
        public int SubTableID { get; set; }
        public double Volume { get; set; }
        public double Price { get; set; }
        public DateTime DateTime { get; set; }
        public string Note { get; set; }
        
        public IAssetID AssetID
        {
            get { return DbUtils.MakeAssetID(AssetTypeID, SubTableID); }
            set { AssetTypeID = value.AssetTypeID; SubTableID = value.ID; }
        }
    }


    public enum OrderStatus
    {
        /// <summary>
        /// indicates that you have transmitted the order, but have not yet received
        /// confirmation that it has been accepted by the order destination.
        /// This order status is not sent by TWS and should be explicitly set by the API developer when an order is submitted.
        /// </summary>
        PendingSubmit,
        /// <summary>
        /// PendingCancel - indicates that you have sent a request to cancel the order
        /// but have not yet received cancel confirmation from the order destination.
        /// At this point, your order is not confirmed canceled. You may still receive
        /// an execution while your cancellation request is pending.
        /// This order status is not sent by TWS and should be explicitly set by the API developer when an order is canceled.
        /// </summary>
        PendingCancel,
        /// <summary>
        /// indicates that a simulated order type has been accepted by the IB system and
        /// that this order has yet to be elected. The order is held in the IB system
        /// (and the status remains DARK BLUE) until the election criteria are met.
        /// At that time the order is transmitted to the order destination as specified
        /// (and the order status color will change).
        /// </summary>
        PreSubmitted,
        /// <summary>
        /// indicates that your order has been accepted at the order destination and is working.
        /// </summary>
        Submitted,
        /// <summary>
        /// indicates that the balance of your order has been confirmed canceled by the IB system.
        /// This could occur unexpectedly when IB or the destination has rejected your order.
        /// </summary>
        Canceled,
        /// <summary>
        /// The order has been completely filled.
        /// </summary>
        Filled,
        /// <summary>
        /// The Order is inactive
        /// </summary>
        Inactive,
        /// <summary>
        /// The order is Partially Filled
        /// </summary>
        PartiallyFilled,
        /// <summary>
        /// Api Pending
        /// </summary>
        ApiPending,
        /// <summary>
        /// Api Cancelled
        /// </summary>
        ApiCancelled,
        /// <summary>
        /// Indicates that there is an error with this order
        /// This order status is not sent by TWS and should be explicitly set by the API developer when an error has occured.
        /// </summary>
        Error,
        /// <summary>
        /// No Order Status
        /// </summary>
        MinFilterSkipped,
        MaxFilterSkipped,
        Unrecognized,
        None
    }

    public class Transaction
    {
        //public int PortfolioID { get; set; }  // not necessary
        public TransactionType TransactionType { get; set; }
        public AssetType AssetTypeID { get; set; }
        public int SubTableID { get; set; }

        public double OldVolume { get; set; }
        public double Volume { get; set; }
        public double Price { get; set; }
        public DateTime DateTime { get; set; }

        //Execution fields
        public int VirtualOrderId { get; set; }
        public OrderStatus OrderStatus { get; set; }
        public double ExecutedVolume { get; set; }
        public double ExecutedAvgPrice { get; set; }

        public IAssetID AssetID
        {
            get { return DbUtils.MakeAssetID(AssetTypeID, SubTableID); }
            set { AssetTypeID = value.AssetTypeID; SubTableID = value.ID; }
        }
    }


    [System.Diagnostics.DebuggerDisplay("{DebugString()}")]
    public class PortfolioPosition
    {
        // There is the Portfolio, and there is the Transaction. Portfolio is a result of Many Transactions.
        //public PortfolioItemTransactionType TransactionType { get; set; } // Transaction shouldn't exist for current PortfolioItem. TransactionType = BuyAsset, SellAsset, Short, Cover, Deposit only means something for Transactions and Orders, not portfolio snapshots of items
        public AssetType AssetTypeID { get; set; }
        public int SubTableID { get; set; }
        public double Volume { get; set; }     // volume can be fractional for Cash and currencies, althought it is Int for stocks in general. That is why it is Int in the Stock table, but it should be more general here
        public double LastSplitAdjustedTransactionPrice { get; set; }   // but it is not dividend adjusted
        public double AveragePrice { get; set; }
        public double FifoPrice { get; set; }
        public DateTime LastTransactionTimeUtc { get; set; }

        // Setter is not public because it is sufficient to be assigned in DBUtils.LoadPortfolio()
        // + in TransactionsAccumulator.Event.GetVirtualPortfolio() to indicate USD-totals in case of multi-currency portfolios
        //public int ID { get; protected internal set; }

        public IAssetID AssetID
        {
            get { return DbUtils.MakeAssetID(AssetTypeID, SubTableID); }
            set { AssetTypeID = value.AssetTypeID; SubTableID = value.ID; }
        }

        public string DebugString()
        {
            return ToString(TickerProvider.Singleton);
        }
        public string ToString(TickerProvider p_tp)
        {
            return $"{AssetID.ToString(p_tp)}[{Volume:F2}]";
            //return String.Format("{0}[{1} {2}]", SizeGroup, AssetID.ToString(p_tp), Size.ToString());
        }
    }
}
