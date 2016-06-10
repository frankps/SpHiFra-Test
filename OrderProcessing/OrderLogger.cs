using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data.Entity.Validation;
using Model = Dvit.OpenBiz.Model;
using Dvit.OpenBiz.Model;
using Dvit.OpenBiz.Database;
using Dvit.OpenBiz.Pcl;
using Dvit.OpenBiz.Reservation;
using NLogLight;
using Dvit.OpenBiz.Services.Reservation;
using Dvit.OpenBiz.Services.Data;
using Dvit.OpenBiz.Services.PaymentGateway;

namespace Dvit.OpenBiz.Services.OrderProcessing
{

    public class LogItems
    {
        //public List<LogItem> Items { get; set; }
        StringBuilder sbLog = new StringBuilder();

        public void Info(string message, params object[] args)
        {
            sbLog.AppendFormat(message, args).AppendLine();
        }

        public void Warn(string message, params object[] args)
        {
            sbLog.AppendFormat(message, args).AppendLine();
        }

        public LogItems()
        {
            //Items = new List<LogItem>();
        }

        public override string ToString()
        {
            return sbLog.ToString();
        }
    }

    public class OrderLogger : OpenBizSvc
    {
        static Logger _Logger = LogManager.GetLogger("OrderLogger");

        public LogItems GetLogItems(Order order, string title = "")
        {
            LogItems items = new LogItems();

            items.Info("Log order: " + title);

            OpenBizContext ctx = new OpenBizContext();

            if (order == null)
            {
                items.Warn("Order is null!");
                return items;
            }

            if (order.OrderLines == null || order.OrderLines.Count() == 0)
            {
                items.Warn("Order has no orderlines!");
                return items;
            }

            int nrOfOrderLines = 0;
            if (order.OrderLines != null)
            {
                nrOfOrderLines = order.OrderLines.Count();
            }

            string orderDate = "";

            if (order.Date.HasValue)
                orderDate = string.Format("date: {0} (ToUt={1} TimeZoneInfoUtc={2})", order.Date, order.Date.Value.ToUniversalTime(), TimeZoneInfo.ConvertTimeToUtc(order.Date.Value));

            items.Info("Log order (order lines: {0}, isAppointment:{1}, A={2},D={3}) {4}", nrOfOrderLines, order.IsAppointment, order.IsActive, order.IsDeleted, orderDate);

            List<string> allProductIds;
          //  List<string> allOrderLineIds;

          //  allOrderLineIds = order.OrderLines.Select(ol => ol.Id).Distinct().ToList();
            allProductIds = order.OrderLines.Select(ol => ol.ProductId).Distinct().ToList();

            var allSubscriptions = ctx.Subscription.Where(s => s.PurchaseOrderId == order.Id);

            var allProducts = ctx.Product.Where(p => allProductIds.Contains(p.Id));

            List<string> allProductOptionIds = new List<string>();

            foreach (OrderLine orderLine in order.OrderLines)
            {
                if (orderLine.OrderLineOptions != null)
                    allProductOptionIds.AddRange(orderLine.OrderLineOptions.Select(olo => olo.ProductOptionId));
            }

            if (order.Appointments != null && order.Appointments.Count() > 0)
            {
                foreach (var app in order.Appointments)
                {
                    items.Info(" -> App: {0} {1}", app.Start, app.End);
                }
            }

            List<ProductOption> allProductOptions = new List<ProductOption>();
            List<ProductOptionValue> allProductOptionValues = new List<ProductOptionValue>();

            if (allProductOptionIds.Count > 0)
            {
                allProductOptions = ctx.ProductOption.Where(po => allProductOptionIds.Contains(po.Id)).ToList();
                allProductOptionValues = ctx.ProductOptionValue.Where(pov => allProductOptionIds.Contains(pov.ProductOptionId)).ToList();
            }

            foreach (OrderLine orderLine in order.OrderLines)
            {
                Product product = null;

                string productName = "?";

                if (orderLine.Product != null)
                {
                    product = orderLine.Product;
                    productName = product.Name;
                }
                else
                {
                    product = allProducts.FirstOrDefault(p => p.Id == orderLine.ProductId);

                    if (product != null)
                        productName = product.Name;
                }

                string optionString = "";

                if (orderLine.OrderLineOptions != null && orderLine.OrderLineOptions.Count() > 0)
                {
                    List<string> optionStrings = new List<string>();

                    foreach (OrderLineOption option in orderLine.OrderLineOptions)
                    {
                        ProductOption prodOpt = allProductOptions.FirstOrDefault(po => po.Id == option.ProductOptionId);

                        var optionName = "unknown";
                        var optionValue = "";

                        if (prodOpt != null)
                        {
                            optionName = prodOpt.Name;

                            if (prodOpt.HasValueObjects)
                            {
                                var prodOptValue = allProductOptionValues.FirstOrDefault(v => v.Id == option.ProductOptionValueId);

                                if (prodOptValue != null)
                                    optionValue = prodOptValue.Name;
                            }
                            else
                            {
                                optionValue = option.ProductOptionValue.ToString();
                            }

                        }

                        string opt = string.Format("{0}={1}", optionName, optionValue);
                        optionStrings.Add(opt);

                        //_Logger.Info("Option id: {0}, value:{1} {2}", option.Id, option.ProductOptionValueId, option.ProductOptionValue);
                    }


                    string sep = "";

                    foreach (string str in optionStrings)
                    {
                        optionString += sep + str;
                        sep = "&";
                    }
                }
                string orderLineLog = string.Format("    {0} x {1}?{2}", orderLine.Quantity, productName, optionString);
                items.Info(orderLineLog);


                if (orderLine.ResourcePlannings != null && orderLine.ResourcePlannings.Count > 0)
                {
                    foreach (var resourcePlanning in orderLine.ResourcePlannings)
                    {
                        items.Info("    -> resourcePlanning {0:dd/MM HH:mm} (UTC={3:HH:mm}) - {1:dd/MM HH:mm}  ({2})", resourcePlanning.Start, resourcePlanning.End, resourcePlanning.ResourceId, resourcePlanning.Start.ToUniversalTime());
                    }
                }
                else
                    items.Info("    -> no resourcePlanning records!");
            }

            if (allSubscriptions != null && allSubscriptions.Count() > 0)
            {
                items.Info("Subscriptions");
                items.Info("=============");
                foreach (Subscription sub in allSubscriptions)
                {
                    items.Info("  Subscription '{0}': {1}/{2}  (€ {3}/{4})  (A:{5},D:{6})", sub.Name, sub.UsedQuantity, sub.TotalQuantity, sub.TotalPayed, sub.TotalPrice, sub.IsActive, sub.IsDeleted);
                }
            }


            return items;
        }

        public void LogOrder(Order order, string title = "")
        {
            
        }

        public void Log(ReservationSearchResult<Order> searchResult)
        {
            int nrOfOptions = 0;

            if (searchResult.Options.Count > 0)
            {
                nrOfOptions = searchResult.Options.Count;
            }

            _Logger.Info("ReservationSearchResult: {0}, nr of options: {1}", searchResult.Status, nrOfOptions);

            foreach (var option in searchResult.Options)
            {
                _Logger.Info("    Option: {0}", option.Date);

                this.LogOrder(option.Order);
            }

        }
    }
}
