using Foundation;
using Plugin.InAppBilling.Abstractions;
using StoreKit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Plugin.InAppBilling
{
    /// <summary>
    /// Implementation for InAppBilling
    /// </summary>
    [Preserve(AllMembers = true)]
    public class InAppBillingImplementation : IInAppBilling
    {
        /// <summary>
        /// Default constructor for In App Billing on iOS
        /// </summary>
        public InAppBillingImplementation()
        {
        }

        public void EnableInAppPurchases(Action<InAppBillingPurchase> onCompleted)
        {
            paymentObserver = new PaymentObserver(onCompleted);
            SKPaymentQueue.DefaultQueue.AddTransactionObserver(paymentObserver);
        }

        /// <summary>
        /// Gets or sets if in testing mode. Only for UWP
        /// </summary>
        public bool InTestingMode { get; set; }

        /// <summary>
        /// Connect to billing service
        /// </summary>
        /// <returns>If Success</returns>
        public Task<bool> ConnectAsync() => Task.FromResult(true);

        /// <summary>
        /// Disconnect from the billing service
        /// </summary>
        /// <returns>Task to disconnect</returns>
        public Task DisconnectAsync() => Task.CompletedTask;

        /// <summary>
        /// Get product information of a specific product
        /// </summary>
        /// <param name="productIds">Sku or Id of the product(s)</param>
        /// <param name="itemType">Type of product offering</param>
        /// <returns></returns>
        public async Task<IEnumerable<InAppBillingProduct>> GetProductInfoAsync(ItemType itemType, params string[] productIds)
        {
            var products = await GetProductAsync(productIds);

            return products.Select(p => new InAppBillingProduct
            {
                LocalizedPrice = p.LocalizedPrice(),
                MicrosPrice = (long)(p.Price.DoubleValue * 1000000d),
                Name = p.LocalizedTitle,
                ProductId = p.ProductIdentifier,
                Description = p.LocalizedDescription,
                CurrencyCode = p.PriceLocale?.CurrencyCode ?? string.Empty
            });
        }

        Task<IEnumerable<SKProduct>> GetProductAsync(string[] productId)
        {
            var productIdentifiers = new NSSet(productId);

            var productRequestDelegate = new ProductRequestDelegate();

            //set up product request for in-app purchase
            var productsRequest = new SKProductsRequest(productIdentifiers)
            {
                Delegate = productRequestDelegate // SKProductsRequestDelegate.ReceivedResponse
            };
            productsRequest.Start();

            return productRequestDelegate.WaitForResponse();
        }


        /// <summary>
        /// Get all current purhcase for a specifiy product type.
        /// </summary>
        /// <param name="itemType">Type of product</param>
        /// <param name="verifyPurchase">Interface to verify purchase</param>
        /// <returns>The current purchases</returns>
        public async Task<IEnumerable<InAppBillingPurchase>> GetPurchasesAsync(ItemType itemType, IInAppBillingVerifyPurchase verifyPurchase = null)
        {
            var purchases = await RestoreAsync();
            var validated = await ValidateReceipt(verifyPurchase);
            return validated ? purchases.Where(p => p != null).Select(p => p.ToIABPurchase()) : null;
        }

        public Task<bool> ValidateReceipt(IInAppBillingVerifyPurchase verifyPurchase)
        {
            if (verifyPurchase == null) return Task.FromResult(false);
            // Get the receipt data for (server-side) validation.
            // See: https://developer.apple.com/library/content/releasenotes/General/ValidateAppStoreReceipt/Introduction.html#//apple_ref/doc/uid/TP40010573
            string receipt = default(string);
            try
            {
                var url = NSBundle.MainBundle.AppStoreReceiptUrl;
                if (!NSFileManager.DefaultManager.FileExists(url.Path))
                {
                    var receiptUrl = NSData.FromUrl(url);
                    if (receiptUrl != null)
                    {
                        receipt = receiptUrl.GetBase64EncodedString(NSDataBase64EncodingOptions.None);
                    }
                }
            } catch(Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
            return verifyPurchase.VerifyPurchase(receipt, string.Empty);
        }

        Task<SKPaymentTransaction[]> RestoreAsync()
        {
            var tcsTransaction = new TaskCompletionSource<SKPaymentTransaction[]>();

            Action<SKPaymentTransaction[]> handler = null;
            handler = new Action<SKPaymentTransaction[]>(transactions =>
            {

                // Unsubscribe from future events
                paymentObserver.TransactionsRestored -= handler;

                if (transactions == null)
                    tcsTransaction.TrySetException(new Exception("Restore Transactions Failed"));
                else
                {

                    tcsTransaction.TrySetResult(transactions);
                }
            });

            paymentObserver.TransactionsRestored += handler;

            // Start receiving restored transactions
            SKPaymentQueue.DefaultQueue.RestoreCompletedTransactions();

            return tcsTransaction.Task;
        }





        /// <summary>
        /// Purchase a specific product or subscription
        /// </summary>
        /// <param name="productId">Sku or ID of product</param>
        /// <param name="itemType">Type of product being requested</param>
        /// <param name="payload">Developer specific payload</param>
        /// <param name="verifyPurchase">Interface to verify purchase</param>
        /// <returns></returns>
        public async Task<InAppBillingPurchase> PurchaseAsync(string productId, ItemType itemType, string payload, IInAppBillingVerifyPurchase verifyPurchase = null)
        {
            var p = await PurchaseAsync(productId);
            if (p == null)
            {
                return null;
            }
            var purchase = p.ToIABPurchase();

            var validated = await ValidateReceipt(verifyPurchase);
            return validated ? purchase : null;
        }

        private InAppBillingPurchase ToIABP(SKPaymentTransaction transaction)
        {
            var reference = new DateTime(2001, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);

            return new InAppBillingPurchase
            {
                TransactionDateUtc = reference.AddSeconds(transaction.TransactionDate.SecondsSinceReferenceDate),
                Id = transaction.TransactionIdentifier,
                ProductId = transaction.Payment?.ProductIdentifier ?? string.Empty,
                State = transaction.GetPurchaseState()
            };
        }

        Task<SKPaymentTransaction> PurchaseAsync(string productId)
        {
            TaskCompletionSource<SKPaymentTransaction> tcsTransaction = new TaskCompletionSource<SKPaymentTransaction>();

            Action<SKPaymentTransaction, bool> handler = null;
            handler = new Action<SKPaymentTransaction, bool>((tran, success) =>
            {


                // Only handle results from this request
                if (productId != tran.Payment.ProductIdentifier)
                    return;

                // Unsubscribe from future events
                paymentObserver.TransactionCompleted -= handler;


                if (!success)
                {
                    if (tran?.Error.Code == 2)
                    {
                        tcsTransaction.TrySetResult(null);
                    }
                    else
                    {
                        tcsTransaction.TrySetException(new Exception(tran?.Error.LocalizedDescription));
                    }
                }
                else
                    tcsTransaction.TrySetResult(tran);
            });

            paymentObserver.TransactionCompleted += handler;

            var payment = SKPayment.CreateFrom(productId);
            SKPaymentQueue.DefaultQueue.AddPayment(payment);

            return tcsTransaction.Task;
        }


        /// <summary>
        /// Consume a purchase with a purchase token.
        /// </summary>
        /// <param name="productId">Id or Sku of product</param>
        /// <param name="purchaseToken">Original Purchase Token</param>
        /// <returns>If consumed successful</returns>
        /// <exception cref="InAppBillingPurchaseException">If an error occures during processing</exception>
		public Task<InAppBillingPurchase> ConsumePurchaseAsync(string productId, string purchaseToken)
        {
            return null;
        }

        /// <summary>
        /// Consume a purchase
        /// </summary>
        /// <param name="productId">Id/Sku of the product</param>
        /// <param name="payload">Developer specific payload of original purchase</param>
        /// <param name="itemType">Type of product being consumed.</param>
        /// <param name="verifyPurchase">Verify Purchase implementation</param>
        /// <returns>If consumed successful</returns>
        /// <exception cref="InAppBillingPurchaseException">If an error occures during processing</exception>
        public Task<InAppBillingPurchase> ConsumePurchaseAsync(string productId, ItemType itemType, string payload, IInAppBillingVerifyPurchase verifyPurchase = null)
        {
            return null;
        }

        PaymentObserver paymentObserver;

        static DateTime NSDateToDateTimeUtc(NSDate date)
        {
            var reference = new DateTime(2001, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);


            return reference.AddSeconds(date?.SecondsSinceReferenceDate ?? 0);
        }
    }


    [Preserve(AllMembers = true)]
    class ProductRequestDelegate : NSObject, ISKProductsRequestDelegate, ISKRequestDelegate
    {
        TaskCompletionSource<IEnumerable<SKProduct>> tcsResponse = new TaskCompletionSource<IEnumerable<SKProduct>>();

        public Task<IEnumerable<SKProduct>> WaitForResponse()
        {
            return tcsResponse.Task;
        }

        [Export("request:didFailWithError:")]
        public void RequestFailed(SKRequest request, NSError error)
        {
            tcsResponse.TrySetException(new Exception(error.LocalizedDescription));
        }

        public void ReceivedResponse(SKProductsRequest request, SKProductsResponse response)
        {
            var product = response.Products;

            if (product != null)
            {
                tcsResponse.TrySetResult(product);
                return;
            }

            tcsResponse.TrySetException(new Exception("Invalid Product"));
        }
    }


    [Preserve(AllMembers = true)]
    class PaymentObserver : SKPaymentTransactionObserver
    {
        public event Action<SKPaymentTransaction, bool> TransactionCompleted;
        public event Action<SKPaymentTransaction[]> TransactionsRestored;

        List<SKPaymentTransaction> restoredTransactions = new List<SKPaymentTransaction>();
        private readonly Action<InAppBillingPurchase> onSuccess;

        public PaymentObserver(Action<InAppBillingPurchase> onSuccess = null)
        {
            this.onSuccess = onSuccess;
        }
        public override void UpdatedTransactions(SKPaymentQueue queue, SKPaymentTransaction[] transactions)
        {
            var rt = transactions.Where(pt => pt.TransactionState == SKPaymentTransactionState.Restored);

            // Add our restored transactions to the list
            // We might still get more from the initial request so we won't raise the event until
            // RestoreCompletedTransactionsFinished is called
            if (rt?.Any() ?? false)
                restoredTransactions.AddRange(rt);

            foreach (SKPaymentTransaction transaction in transactions)
            {
                Debug.WriteLine($"Found transaction state: {transaction.TransactionState} id: {transaction.TransactionIdentifier}");
                switch (transaction.TransactionState)
                {
                    case SKPaymentTransactionState.Purchased:

                        if (TransactionCompleted != null)
                        {
                            TransactionCompleted.Invoke(transaction, true);
                        }
                        else
                        {
                            onSuccess?.Invoke(transaction.ToIABPurchase());
                        }
                        Debug.WriteLine($"Transaction purchased finished id: {transaction.TransactionIdentifier}");
                        SKPaymentQueue.DefaultQueue.FinishTransaction(transaction);
                        break;
                    case SKPaymentTransactionState.Failed:
                        TransactionCompleted?.Invoke(transaction, false);
                        SKPaymentQueue.DefaultQueue.FinishTransaction(transaction);
                        break;
                    default:
                        break;
                }
            }
        }

        public override void RestoreCompletedTransactionsFinished(SKPaymentQueue queue)
        {
            // This is called after all restored transactions have hit UpdatedTransactions
            // at this point we are done with the restore request so let's fire up the event
            var rt = restoredTransactions.ToArray();
            // Clear out the list of incoming restore transactions for future requests
            restoredTransactions.Clear();

            TransactionsRestored?.Invoke(rt);

            foreach (var transaction in rt)
            {
                Debug.WriteLine($"Transaction restored finished id: {transaction.TransactionIdentifier}");
                SKPaymentQueue.DefaultQueue.FinishTransaction(transaction);
            }
        }

        public override void RestoreCompletedTransactionsFailedWithError(SKPaymentQueue queue, Foundation.NSError error)
        {
            // Failure, just fire with null
            TransactionsRestored?.Invoke(null);
        }
    }



    [Preserve(AllMembers = true)]
    static class SKTransactionExtensions
    {

        public static InAppBillingPurchase ToIABPurchase(this SKPaymentTransaction transaction)
        {
            var p = transaction.OriginalTransaction;
            if (p == null)
                p = transaction;

            if (p == null)
                return null;

            var newP = new InAppBillingPurchase
            {
                TransactionDateUtc = NSDateToDateTimeUtc(p.TransactionDate),
                Id = p.TransactionIdentifier,
                ProductId = p.Payment?.ProductIdentifier ?? string.Empty,
                State = p.GetPurchaseState()
            };

            return newP;
        }

        static DateTime NSDateToDateTimeUtc(NSDate date)
        {
            var reference = new DateTime(2001, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);

            return reference.AddSeconds(date?.SecondsSinceReferenceDate ?? 0);
        }

        public static PurchaseState GetPurchaseState(this SKPaymentTransaction transaction)
        {
            switch (transaction.TransactionState)
            {
                case SKPaymentTransactionState.Restored:
                    return Abstractions.PurchaseState.Restored;
                case SKPaymentTransactionState.Purchasing:
                    return Abstractions.PurchaseState.Purchasing;
                case SKPaymentTransactionState.Purchased:
                    return Abstractions.PurchaseState.Purchased;
                case SKPaymentTransactionState.Failed:
                    return Abstractions.PurchaseState.Failed;
                case SKPaymentTransactionState.Deferred:
                    return Abstractions.PurchaseState.Deferred;
            }

            return Abstractions.PurchaseState.Unknown;
        }


    }


    [Preserve(AllMembers = true)]
    static class SKProductExtension
    {
        /// <remarks>
        /// Use Apple's sample code for formatting a SKProduct price
        /// https://developer.apple.com/library/ios/#DOCUMENTATION/StoreKit/Reference/SKProduct_Reference/Reference/Reference.html#//apple_ref/occ/instp/SKProduct/priceLocale
        /// Objective-C version:
        ///    NSNumberFormatter *numberFormatter = [[NSNumberFormatter alloc] init];
        ///    [numberFormatter setFormatterBehavior:NSNumberFormatterBehavior10_4];
        ///    [numberFormatter setNumberStyle:NSNumberFormatterCurrencyStyle];
        ///    [numberFormatter setLocale:product.priceLocale];
        ///    NSString *formattedString = [numberFormatter stringFromNumber:product.price];
        /// </remarks>
        public static string LocalizedPrice(this SKProduct product)
        {
            var formatter = new NSNumberFormatter()
            {
                FormatterBehavior = NSNumberFormatterBehavior.Version_10_4,
                NumberStyle = NSNumberFormatterStyle.Currency,
                Locale = product.PriceLocale
            };
            var formattedString = formatter.StringFromNumber(product.Price);
            Console.WriteLine(" ** formatter.StringFromNumber(" + product.Price + ") = " + formattedString + " for locale " + product.PriceLocale.LocaleIdentifier);
            return formattedString;
        }
    }
}