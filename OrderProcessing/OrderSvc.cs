using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data.Entity;
using System.Data.Entity.Validation;
using Model = Dvit.OpenBiz.Model;
using Dvit.OpenBiz.Model;
using Dvit.OpenBiz.Database;
using Dvit.OpenBiz.Pcl;
using Dvit.OpenBiz.Reservation;
using NLogLight;
using Dvit.OpenBiz.Services.Reservation;
using Dvit.OpenBiz.Services.Data;
using System.Collections.Concurrent;
using Dvit.OpenBiz.Azure.Storage;
using Dvit.OpenBiz.Services.Common;
using System.Web.Hosting;

namespace Dvit.OpenBiz.Services.OrderProcessing
{
    public class OrderSvc
    {
        InMemoryCache cache = new InMemoryCache();

        static Logger _Logger = LogManager.GetLogger("OrderSvc");

        public Order GetOrder(string orderId, OpenBizContext ctx = null)
        {
            if (ctx == null)
                ctx = new OpenBizContext();

            Order order = ctx.Order.Include("OrderLines.OrderLineOptions").Include("OrderLines.ResourcePlannings").Include("OrderLines.Product").Include("OrderTaxes").FirstOrDefault(o => o.Id == orderId);
            // .Include("OrderLines.ResourcePlannings")

            if (order.Date.HasValue)
                order.Date = DateTime.SpecifyKind(order.Date.Value, DateTimeKind.Utc) ;


            if (order != null)
            {
                ctx.Entry(order).Collection(o => o.Payments).Load();
                ctx.Entry(order).Collection(o => o.Appointments).Load();
                 if (order.Appointments != null)
                {
                    foreach (var app in order.Appointments)
                    {
                        app.Start = DateTime.SpecifyKind(app.Start, DateTimeKind.Utc);
                        app.End = DateTime.SpecifyKind(app.End, DateTimeKind.Utc);
                    }
                }

                if (order.Appointments != null)
                {
                    foreach (var app in order.Appointments)
                    {
                        ctx.Entry(app).Collection(a => a.ResourcePlannings).Load();
                    }
                }

                //if (order.Payments != null)
                //{
                //    foreach(var payment in order.Payments)
                //    {
                //        payment.Date = DateTime.SpecifyKind(payment.Date, DateTimeKind.Utc);
                //    }
                //}
            }
            return order;
        }

        private void DeleteItems(Order orderToUpdate, Order orderFromDb, OpenBizContext ctx = null)
        {
            List<OrderLine> orderLinesToDelete = new List<OrderLine>();

            if (orderToUpdate.OrderLines != null)
                orderToUpdate.OrderLines.Where(ol => ol.IsDeleted).ToList();

            List<string> activeOrderLineIds = new List<string>();

            if (orderToUpdate.OrderLines != null)
                activeOrderLineIds.AddRange(orderToUpdate.OrderLines.Where(ol => !ol.IsDeleted).Select(ol => ol.Id));

            if (orderFromDb.OrderLines != null)
                orderLinesToDelete.AddRange(orderFromDb.OrderLines.Where(olDb => !activeOrderLineIds.Contains(olDb.Id)));

            if (orderLinesToDelete.Count > 0)
            {
                List<string> orderLineIdsToDelete = orderLinesToDelete.Select(ol => ol.Id).ToList();

                List<OrderLine> dbOrderLinesToDelete = orderFromDb.OrderLines.Where(olDb => orderLineIdsToDelete.Contains(olDb.Id)).ToList();

                dbOrderLinesToDelete.ForEach(olToDelete =>
                {
                    ctx.DeleteOrderLine(olToDelete);
                });
            }
        }

        public void RemoveDuplicateOrderLineOptions()
        {

        }

        public UpdateResult<Order> UpdateOrder(Order orderToUpdate, OpenBizContext ctx = null)
        {
            try
            {
                if (ctx == null)
                    ctx = new OpenBizContext();

                var orderFromDb = this.GetOrder(orderToUpdate.Id, ctx);

                if (orderFromDb == null)
                    return new UpdateResult<Order>(UpdateResultStatus.OK);

                string appointmentIdInDb = null;

                if (orderFromDb.Appointments != null && orderFromDb.Appointments.Count > 0)
                {
                    appointmentIdInDb = orderFromDb.Appointments.FirstOrDefault().Id;
                }

                bool createInvoiceNumber = false;
                if (orderToUpdate.IsInvoiced && !orderFromDb.IsInvoiced && string.IsNullOrWhiteSpace(orderToUpdate.InvoiceNumber))
                    createInvoiceNumber = true;


                orderToUpdate.CopyDataTo(orderFromDb);

                if (orderToUpdate.OrderLines == null)
                    orderToUpdate.OrderLines = new List<OrderLine>();


                // re-activate previously deleted orders
                if (!orderFromDb.IsActive || orderFromDb.IsDeleted)
                {
                    if (orderToUpdate.IsActive && !orderToUpdate.IsDeleted)
                    {
                        orderFromDb.IsActive = true;
                        orderFromDb.IsDeleted = false;

                        ObjectSvc stateSvc = new ObjectSvc();

                        ObjectState newState = new ObjectState()
                        {
                            ObjectId = orderToUpdate.Id,
                            ObjectType = "order",
                            State = State.Activated
                        };

                        stateSvc.RegisterState(newState, applyLogicToObject: false, ctx: ctx);
                    }
                }

                // First delete removed orderlines
                DeleteItems(orderToUpdate, orderFromDb, ctx);

                Appointment appFromDb = null;

                if (orderToUpdate.Appointments != null)
                {
                    foreach (var appToUpdate in orderToUpdate.Appointments)
                    {
                        appFromDb = orderFromDb.Appointments.FirstOrDefault(a => a.Id == appToUpdate.Id);

                        if (appFromDb == null)
                        {
                            appFromDb = Appointment.CreateNew();
                            appFromDb.Id = appToUpdate.Id;
                            appToUpdate.CopyDataTo(appFromDb);
                            appFromDb.OrderId = orderFromDb.Id;
                            appFromDb.Order = orderFromDb;
                            ctx.Appointment.Add(appFromDb);
                        }
                        else
                        {
                            appToUpdate.CopyDataTo(appFromDb);
                        }
                    }
                }

                ctx.SaveChanges();



                List<OrderLine> orderLinesToUpdate = orderToUpdate.OrderLines.Where(ol => !ol.IsDeleted).ToList();

                // Perform updates
                foreach (var orderLineToUpdate in orderLinesToUpdate)
                {
                    var orderLineFromDb = orderFromDb.OrderLines.FirstOrDefault(ol => ol.Id == orderLineToUpdate.Id);


                    if (orderLineFromDb == null)
                    {
                        OrderLine newOrderLine = orderLineToUpdate.CloneOld(new OrderLine(false, false));
                        newOrderLine.Id = orderLineToUpdate.Id;
                        newOrderLine.OrderId = orderToUpdate.Id;

                        orderFromDb.OrderLines.Add(newOrderLine);
                        //ctx.OrderLine.Add(newOrderLine);
                    }
                    else
                    {
                        if (orderLineFromDb.Quantity != orderLineToUpdate.Quantity)
                            orderLineFromDb.Quantity = orderLineToUpdate.Quantity;

                        if (orderLineFromDb.Persons != orderLineToUpdate.Persons)
                            orderLineFromDb.Persons = orderLineToUpdate.Persons;

                        if (orderLineFromDb.IsCustomPrice != orderLineToUpdate.IsCustomPrice)
                            orderLineFromDb.IsCustomPrice = orderLineToUpdate.IsCustomPrice;

                        if (orderLineFromDb.UnitPrice != orderLineToUpdate.UnitPrice)
                            orderLineFromDb.UnitPrice = orderLineToUpdate.UnitPrice;

                        orderLineFromDb.LineNumber = orderLineToUpdate.LineNumber;
                        orderLineFromDb.PlanningOrder = orderLineToUpdate.PlanningOrder;
                        orderLineFromDb.FulfilledQuantity = orderLineToUpdate.FulfilledQuantity;
                        orderLineFromDb.TotalPrice = orderLineToUpdate.TotalPrice;
                        orderLineFromDb.TaxPctg = orderLineToUpdate.TaxPctg;
                        orderLineFromDb.Tax = orderLineToUpdate.Tax;
                        orderLineFromDb.StartDate = orderLineToUpdate.StartDate;
                    }

                    ctx.SaveChanges();
                    /*if (orderLineFromDb.TotalPrice != orderLineToUpdate.TotalPrice)
                        orderLineFromDb.TotalPrice = orderLineToUpdate.TotalPrice;

                    if (orderLineFromDb.Tax != orderLineToUpdate.Tax)
                        orderLineFromDb.Tax = orderLineToUpdate.Tax;*/

                    OrderLineOption orderLineOptionFromDb = null;

                    if (orderLineToUpdate.OrderLineOptions != null)
                    {   /*
                        foreach (var orderLineOptionToDelete in orderLineToUpdate.OrderLineOptions.Where(olo => !olo.IsActive || olo.IsDeleted))
                        {
                            if (!string.IsNullOrWhiteSpace(orderLineOptionToDelete.Id))
                            {
                                ctx.OrderLineOption.Remove()
                            }
                        }
                        */

                        List<string> productOptionsId = new List<string>();

                        foreach (var orderLineOptionToUpdate in orderLineToUpdate.OrderLineOptions.ToList())
                        {
                            if (orderLineFromDb != null && orderLineFromDb.OrderLineOptions != null)
                                orderLineOptionFromDb = orderLineFromDb.OrderLineOptions.FirstOrDefault(olo => olo.Id == orderLineOptionToUpdate.Id);


                            // remove duplicate orderline options (same product option Id)
                            if (productOptionsId.Contains(orderLineOptionToUpdate.ProductOptionId))
                            {
                                orderLineToUpdate.OrderLineOptions.Remove(orderLineOptionToUpdate);

                                if (orderLineOptionFromDb != null)
                                    ctx.OrderLineOption.Remove(orderLineOptionFromDb);

                                continue;
                            }

                            if (orderLineOptionToUpdate.IsActive && !orderLineOptionToUpdate.IsDeleted)
                            {
                                if (orderLineOptionFromDb == null)
                                {
                                    OrderLineOption newOrderLineOption = orderLineOptionToUpdate.CloneOld();
                                    newOrderLineOption.OrderLineId = orderLineToUpdate.Id;
                                    ctx.OrderLineOption.Add(newOrderLineOption);
                                }
                                else
                                {
                                    if (orderLineOptionFromDb.ProductOptionValue != orderLineOptionToUpdate.ProductOptionValue)
                                        orderLineOptionFromDb.ProductOptionValue = orderLineOptionToUpdate.ProductOptionValue;

                                    if (orderLineOptionFromDb.ProductOptionValueId != orderLineOptionToUpdate.ProductOptionValueId)
                                        orderLineOptionFromDb.ProductOptionValueId = orderLineOptionToUpdate.ProductOptionValueId;
                                }

                                productOptionsId.Add(orderLineOptionToUpdate.ProductOptionId);
                            }
                            else // inactive or deleted option
                            {
                                if (orderLineOptionFromDb != null)
                                {
                                    ctx.OrderLineOption.Remove(orderLineOptionFromDb);
                                }
                            }


                        }
                    }

                    //throws error if not checked on null
                    if (orderLineToUpdate.ResourcePlannings != null)
                    {
                        foreach (var resourcePlanningToUpdate in orderLineToUpdate.ResourcePlannings)
                        {
                            ResourcePlanning resourcePlanningFromDb = null;

                            if (orderLineFromDb != null && orderLineFromDb.ResourcePlannings != null)
                                resourcePlanningFromDb = orderLineFromDb.ResourcePlannings.FirstOrDefault(rp => rp.Id == resourcePlanningToUpdate.Id);

                            if (resourcePlanningFromDb == null)
                            {
                                ResourcePlanning newResourcePlanning = resourcePlanningToUpdate.CloneOld();
                                newResourcePlanning.OrderLineId = orderLineToUpdate.Id;
                                ctx.ResourcePlanning.Add(newResourcePlanning);
                            }
                            else
                            {
                                resourcePlanningToUpdate.CopyDataTo(resourcePlanningFromDb);
                            }
                        }
                    }
                }

                SetOrderLinePrices(orderFromDb, ctx);
                CalculateTotals(orderFromDb);

                if (orderFromDb.IsGift)
                    CreateOrUpdateGift(orderFromDb, ctx);

                UpdatePayments(orderToUpdate, orderFromDb, ctx);
                UpdateFulfillmentStatus(orderFromDb);


                if (createInvoiceNumber)
                    MakeInvoice(orderFromDb);


                if (appFromDb != null)
                    SetAppointmentInfo(appFromDb);

                ctx.SaveChanges();


                ProcessSubscriptionOrder(orderToUpdate, orderFromDb, ctx);

                // ProcessCreditUploadOrder(orderToUpdate, orderFromDb, productsDictionary, ctx);


                //  orderFromDb = this.GetOrder(orderToUpdate.Id);

                return new UpdateResult<Order>(UpdateResultStatus.OK, orderFromDb);
            }
            catch (DbEntityValidationException ex)
            {
                Log(ex, _Logger);

                Console.Out.WriteLine(ex.ToString());
                throw;
            }
            catch (Exception ex)
            {
                _Logger.Error(ex.ToString());
                Console.Out.WriteLine(ex.ToString());
                throw;
            }
        }

        private string CreateUniqueGiftCode(string organisationId, OpenBizContext ctx)
        {
            string code = null;

            do
            {
                Guid guid = Guid.NewGuid();
                code = guid.ToString().Substring(0, 5).ToUpper();

            } while (ctx.Gift.Where(g => (g.GiftCode == code && g.Order.OrganisationId == organisationId)).Count() > 0);

            return code;
        }

        private bool MakeInvoice(Order orderFromDb)
        {
            // invoice number must be empty
            if (!string.IsNullOrWhiteSpace(orderFromDb.InvoiceNumber))
                return false;

            if (!orderFromDb.InvoiceDate.HasValue)
            {
                if (orderFromDb.Date.HasValue)
                    orderFromDb.InvoiceDate = orderFromDb.Date;
                else
                    orderFromDb.InvoiceDate = DateTime.Now;
            }

            orderFromDb.InvoiceNumber = GetNextInvoiceNumber(orderFromDb.OrganisationId, orderFromDb.InvoiceDate.Value);

            return true;
        }

        private string GetNextInvoiceNumber(string orgId, DateTime invoiceDate)
        {
            OpenBizContext ctx = new OpenBizContext();

            var org = ctx.Organisation.FirstOrDefault(o => o.Id == orgId);

            int invoiceNumber = org.InvoiceNextNumber++;

            string formatString = "{0:yy}.{1:000}";

            string strInvoiceNumber = string.Format(formatString, invoiceDate, invoiceNumber);

            ctx.SaveChanges();

            return strInvoiceNumber;
        }


        private void AutoDetectGift(Order order, ICollection<Payment> payments, Dictionary<string, Product> productsDictionary, OpenBizContext ctx = null)
        {
            if (order == null)
                return;

            if (!order.IsGift && order.OrderLines != null && order.OrderLines.Count() == 1)
            {
                // if the order contains exactly 1 product (gift), then we will convert it to a gift

                OrderLine orderLine = order.OrderLines.FirstOrDefault();

                if (!productsDictionary.ContainsKey(orderLine.ProductId))
                    return;

                Product product = productsDictionary[orderLine.ProductId];

                if (product != null && !string.IsNullOrWhiteSpace(product.SystemTag))
                {
                    if (product.SystemTag.ToLower().Contains("gift"))
                    {
                        order.IsGift = true;

                        if (payments != null)
                        {
                            decimal totalPayedAmount = payments.Sum(p => p.Amount);

                            if (totalPayedAmount > orderLine.UnitPrice)
                            {
                                orderLine.UnitPrice = totalPayedAmount;
                                orderLine.IsCustomPrice = true;
                            }
                        }
                    }
                }
            }
            else if (order.IsGift && (order.OrderLines == null || order.OrderLines.Count() == 0))
            {
                // order is a gift, but contains no products => we automatically add the gift product

                Product giftProduct = ctx.Product.FirstOrDefault(p => p.Catalog.OrganisationId == order.OrganisationId && p.SystemTag == "gift");

                if (giftProduct != null)
                {
                    OrderLine orderLine = OrderLine.CreateNew();
                    orderLine.ProductId = giftProduct.Id;
                    orderLine.Quantity = 1;
                    //order.OrderLines.Add(orderLine);
                    order.Add(orderLine);

                    if (payments != null)
                    {
                        decimal totalPayedAmount = payments.Sum(p => p.Amount);

                        orderLine.UnitPrice = totalPayedAmount;
                        orderLine.IsCustomPrice = true;
                    }
                }
            }
        }

        private Gift CreateOrUpdateGift(Order order, OpenBizContext ctx, bool isNewOrder = false)
        {
            Gift gift = null;

            if (!isNewOrder)
                gift = ctx.Gift.FirstOrDefault(g => g.OrderId == order.Id);

            if (gift == null)
            {
                gift = Gift.CreateNew();

                ctx.Gift.Add(gift);
                gift.OrderId = order.Id;

                string giftCode = CreateUniqueGiftCode(order.OrganisationId, ctx);
                gift.OrganisationId = order.OrganisationId;

                gift.GiftCode = giftCode;
                order.GiftCode = giftCode;
            }
            else
            {
                gift.IsDeleted = false;
                gift.IsActive = true;
            }

            gift.Value = order.GrandTotal;

            return gift;
        }

        // public void CalculatePrice(Order order, OpenBizContext ctx)
        public void SetOrderLinePrices(Order order, OpenBizContext ctx)
        {
            if (order == null)
                return;

            _Logger.Trace("Start calculating price for order: {0}", order.Id);

            OrderPricingContext orderPricingCtx = new OrderPricingContext(order, ctx);

            order.TaxIncluded = orderPricingCtx.IsTaxInclusiveOrder();

            if (order.OrderLines != null)
            {
                foreach (OrderLine ol in order.OrderLines)
                {
                    orderPricingCtx.SetPrice(ol);

                }
            }

            _Logger.Trace("Calculated price: {0}", order.GrandTotal);
        }

        public void CalculateAdvance(Order order)
        {
            Organisation org = cache.GetOrganisation(order.OrganisationId);
            Party party = cache.GetParty(order.PartyId);

            if (!org.AdvanceActivated || party.AdvanceFactor == 0)
            {
                order.Advance = 0;
                return;
            }

            decimal totalAdvance = 0;

            foreach (var orderLine in order.OrderLines)
            {
                decimal advancePctg = 0;

                Product product = cache.GetProduct(orderLine.ProductId);
                Catalog catalog = cache.GetCatalog(product.CatalogId);

                if (product.AdvancePctg.HasValue)
                {
                    advancePctg = product.AdvancePctg.Value;
                }
                else
                {
                    advancePctg = catalog.AdvancePctg;
                }

                totalAdvance += orderLine.TotalPrice * advancePctg;
            }

            totalAdvance = totalAdvance * party.AdvanceFactor;

            if (totalAdvance < 0)
                totalAdvance = 0;
            else
                totalAdvance = Math.Ceiling(totalAdvance / 5) * 5;  // round to a multiplicity of 5

            if (totalAdvance > order.GrandTotal)
                totalAdvance = order.GrandTotal;

            order.Advance = totalAdvance;

            return;
        }

        public void CalculateTotals(Order order)
        {
            if (order == null)
                return;

            _Logger.Trace("Start calculating totals for order: {0}", order.Id);

            order.SubTotal = 0;
            order.SubTotalInclTax = 0;
            order.GrandTotal = 0;

            decimal subTotal = 0;
            decimal subTotalInclTax = 0;

            if (order.OrderLines != null)
            {
                foreach (OrderLine ol in order.OrderLines)
                {
                    subTotal += ol.CalculateTotalExclTax();
                    subTotalInclTax += ol.CalculateTotalInclTax();
                }
            }

            order.SubTotal = Math.Round(subTotal, 2);
            order.SubTotalInclTax = Math.Round(subTotalInclTax, 2);

            order.TotalTax = order.SubTotalInclTax - order.SubTotal;
            order.GrandTotal = order.SubTotalInclTax;

            _Logger.Trace("Calculated price: {0}", order.GrandTotal);
        }


        private void AttachProducts(Order order, Dictionary<string, Product> productsDictionary)
        {
            if (order.OrderLines == null)
                return;

            foreach (OrderLine ol in order.OrderLines.Where(ol => ol.Product == null))
            {
                if (productsDictionary.ContainsKey(ol.ProductId))
                {
                    ol.Product = productsDictionary[ol.ProductId];
                }
            }
        }

        public OrderCreateResult<Order> CreateOrder(Order order)
        {
            try
            {
                StopwatchResults stopwatchResults = new StopwatchResults();
                StringBuilder sbLog = new StringBuilder();

                OpenBizContext ctx = new OpenBizContext();

                Appointment app = null;
                Order newOrder = null;


                //Dictionary<string, Product> productsDictionary = cache.GetProductsAsDictionary(order.OrderLines.Select(ol => ol.ProductId));

                OrderWithContext orderCtx = new OrderWithContext(order, cache);



                //   AttachProducts(order, productsDictionary);

                StopwatchUtil.Time("Creating appointment", stopwatchResults, () =>
                {
                    order.RemoveDeletedItems();

                    List<ResourcePlanning> plannings = order.GetResourcePlannings();

                    /*
                    if (plannings == null || plannings.Count == 0)
                    {
                        _Logger.Info("Can't calculate end date: No resource plannings are available!");
                        return; // new OrderCreateResult<Order>(OrderCreateResultStatus.NoResourcePlanningsAvailable);
                    }*/

                    if (order.IsAppointment && plannings != null && plannings.Count > 0)
                    {
                        app = order.CreateAppointment();
                        ctx.Appointment.Add(app);

                        DateTime appEnd = order.Date.Value;



                        appEnd = plannings.Max(p => p.End);

                        app.OrderId = null;
                        app.Order = null;
                        app.End = appEnd;

                    }
                    else
                    {
                        // if order.IsAppointment was true, but there were no resource plannings, then we set to false
                        order.IsAppointment = false;
                    }

                });

                StopwatchUtil.Time("Saving appointment", stopwatchResults, () =>
                {
                    if (order.IsAppointment)
                        ctx.SaveChanges();
                });


                StopwatchUtil.Time("Creating the order", stopwatchResults, () =>
                {
                    Order orderGraph = null;

                    if (order.IsAppointment)
                        orderGraph = Order.GetGraph(true, true, true);
                    else
                        orderGraph = Order.GetGraph(true, true, false);

                    newOrder = order.Clone(orderGraph);

                    if (order.IsAppointment)
                        newOrder.LinkWithAppointment(app);

                    //string orderTitle = ctx.GetOrderTitle(newOrder);

                    //string orderTitle = "title";


                    // string orderTitle = newOrder.Title;     
                    if (string.IsNullOrWhiteSpace(newOrder.Title))
                        newOrder.Title = "/";

                    if (app != null)
                        app.Title = newOrder.Title;

                    //  ctx.DetachStaticObjects(newOrder);
                    ctx.Order.Add(newOrder);

                    AutoDetectGift(newOrder, order.Payments, orderCtx.ProductsDictionary, ctx);




                    SetOrderLinePrices(newOrder, ctx);

                    CalculateTotals(newOrder);

                    /* Orders coming from the web app (customer orders) had no advance specified                      
                     */
                    if (order.OrderType == OrderType.CustomerOrder)
                    {
                        this.CalculateAdvance(newOrder);
                    }

                    if (newOrder.IsGift)
                        CreateOrUpdateGift(newOrder, ctx);

                    if (newOrder.IsInvoiced && string.IsNullOrWhiteSpace(newOrder.InvoiceNumber))
                    {
                        MakeInvoice(newOrder);
                    }
                });


                StopwatchUtil.Time("Saving the order", stopwatchResults, () =>
                {
                    ctx.SaveChanges();
                });

                StopwatchUtil.Time("Update payments & set descriptions", stopwatchResults, () =>
                {
                    UpdatePayments(order, newOrder, ctx);

                    if (app != null)
                        SetAppointmentInfo(app);

                    ctx.SaveChanges();
                });

                StopwatchUtil.Time("Perform logic", stopwatchResults, () =>
                {
                    ProcessSubscriptionOrder(null, newOrder, ctx);

                    ProcessCreditUploadOrder(order, newOrder, orderCtx.ProductsDictionary, ctx);

                    
                });

                stopwatchResults.LogResult(sbLog);

                Console.Out.WriteLine(sbLog.ToString());

                /*
                if (order.PartyId != null)
                {
                    ctx.UpdateSysSyncPartyWithPublicPartyId("order", order.PartyId);

                    if (order.IsAppointment)
                        ctx.UpdateSysSyncPartyWithPublicPartyId("appointment", order.PartyId);
                }
                var orderFromDb = this.GetOrder(newOrder.Id);
                */
                return new OrderCreateResult<Order>(OrderCreateResultStatus.OK, newOrder);
            }
            catch (DbEntityValidationException ex)
            {
                Log(ex, _Logger);

                Console.Out.WriteLine(ex.ToString());
                throw;
            }
            catch (Exception ex)
            {
                _Logger.Error(ex.ToString());
                Console.Out.WriteLine(ex.ToString());
                throw;
            }
        }

        public void OnOrderCreated(string orderId)
        {
            OpenBizContext ctx = new OpenBizContext();

            Order order = this.GetOrder(orderId);

            if (order != null)
                this.SendMessage(order, ctx);

            ctx.SaveChanges();
        }

        public void SendMessage(Order order, OpenBizContext ctx)
        {
            Message msg = Message.CreateNew();

            msg.OrganisationId = order.OrganisationId;
            msg.ToPartyId = order.PartyId;
            msg.ObjectId = order.Id;
            msg.ObjectType = "order";
            msg.To = "test@test.com";
            msg.CommType = "email";
            msg.Subject = "Nieuw order";
            msg.BodyText = "Wij danken u voor uw order...";
            msg.IsProcessed = true;

            ctx.Message.Add(msg);
        }

        private bool UpdateFinAccount(string finAccountId, decimal amount, OpenBizContext ctx)
        {
            if (string.IsNullOrWhiteSpace(finAccountId))
                return false;

            var finAccount = ctx.FinAccount.FirstOrDefault(a => a.Id == finAccountId);

            if (finAccount == null)
                return false;

            finAccount.Amount += amount;

            return true;
        }

        public void UpdatePayments(Order orderToUpdate, Order orderFromDb, OpenBizContext ctx)
        {
            bool orderPricingChanged = false;

            orderFromDb.PaymentStatus = (short)PaymentStatus.NotPaid;

            if (orderToUpdate.Payments == null || orderToUpdate.Payments.Count == 0)
                return;

            if (orderFromDb != null)
                ctx.Entry(orderFromDb).Collection(o => o.Payments).Load();



            /* Deleted payments
             * ================
             */
            foreach (var paymentToDelete in orderToUpdate.Payments.Where(p => p.IsDeleted || !p.IsActive))
            {
                var paymentFromDb = orderFromDb.Payments.FirstOrDefault(p => p.Id == paymentToDelete.Id);

                if (paymentFromDb == null)
                    continue;

                switch ((PaymentType)paymentFromDb.PaymentType)
                {
                    case PaymentType.AccountWithdrawal:
                        UpdateFinAccount(paymentToDelete.FinAccountId, paymentToDelete.Amount, ctx);
                        break;

                    case PaymentType.Gift:
                        Gift gift = ctx.Gift.FirstOrDefault(g => g.Id == paymentFromDb.GiftId);

                        // the value needs to become available again on the gift!
                        paymentFromDb.Amount = -paymentFromDb.Amount;
                        PayFromGift(paymentFromDb, gift);
                        break;

                    case PaymentType.Subscription:
                        Subscription subscription = ctx.Subscription.FirstOrDefault(s => s.Id == paymentFromDb.SubscriptionId);

                        if (subscription != null)
                        {
                            subscription.UsedQuantity--;
                        }
                        break;
                }

                /*
                if (paymentFromDb.PaymentType == (short)PaymentType.AccountWithdrawal)
                {
                    
                }
                else if (paymentFromDb.PaymentType == (short)PaymentType.Gift)
                {
                    Gift gift = ctx.Gift.FirstOrDefault(g => g.Id == paymentFromDb.GiftId);

                    // the value needs to become available again on the gift!
                    paymentFromDb.Amount = -paymentFromDb.Amount;
                    PayFromGift(paymentFromDb, gift);
                } 
                */

                ctx.Payment.Remove(paymentFromDb);
            }

            /* Group gift payments (of same gift)
             * ==================================
             */

            /*
            var paymentsSameGift = orderToUpdate.Payments.GroupBy(p => new { GiftId = p.GiftId })
                .Select(g => new { GiftId = g.Key.GiftId, GiftCount = g.Count() })
                .Where(g => g.GiftCount > 0).ToList();

            if (paymentsSameGift.Count() > 0)
            {
                foreach (var giftPays in paymentsSameGift)
                {

                }
            }
            */


            foreach (var paymentToUpdate in orderToUpdate.Payments.Where(p => !p.IsDeleted && p.IsActive))
            {
                Payment paymentFromDb = null;

                if (orderFromDb != null)
                    paymentFromDb = orderFromDb.Payments.FirstOrDefault(p => p.Id == paymentToUpdate.Id);

                bool payOk = true;

                if (paymentFromDb == null)  // new payments
                {
                    paymentFromDb = Payment.CreateNew();
                    paymentFromDb.OrderId = orderFromDb.Id;
                    paymentFromDb.Date = DateTime.Now;
                    paymentFromDb.Amount = paymentToUpdate.Amount;

                    /*
                    if (paymentToUpdate.PaymentType == (short)PaymentType.AccountWithdrawal)
                    {


                    }
                    else if (paymentToUpdate.PaymentType == (short)PaymentType.Gift)
                    {

                    }*/

                    switch ((PaymentType)paymentToUpdate.PaymentType)
                    {
                        case PaymentType.AccountWithdrawal:

                            paymentFromDb.FinAccountId = paymentToUpdate.FinAccountId;
                            payOk = UpdateFinAccount(paymentToUpdate.FinAccountId, -paymentToUpdate.Amount, ctx);
                            break;

                        case PaymentType.Gift:

                            Gift gift = ctx.Gift.FirstOrDefault(g => g.Id == paymentToUpdate.GiftId);
                            PayFromGift(paymentToUpdate, gift);
                            break;

                        case PaymentType.Subscription:

                            Subscription subscription = ctx.Subscription.Where(s => s.Id == paymentToUpdate.SubscriptionId && s.UsedQuantity < s.TotalQuantity).OrderByDescending(s => s.UsedQuantity).FirstOrDefault();

                            if (subscription != null && subscription.UsedQuantity < subscription.TotalQuantity)
                            {
                                subscription.UsedQuantity++;

                                if (subscription.UsedQuantity == 1)
                                    subscription.FirstUsedOn = DateTime.Now;

                                paymentFromDb.SubscriptionId = subscription.Id;

                                paymentFromDb.Amount = subscription.TotalPrice / subscription.TotalQuantity;

                                // find the orderline matching this subscription
                                OrderLine subscriptionOrderLine = this.GetOrderLineForSubscription(orderFromDb, subscription);

                                if (subscriptionOrderLine != null)  // && subscriptionOrderLine.UnitPrice != paymentFromDb.Amount
                                {
                                    orderPricingChanged = true;

                                    subscriptionOrderLine.UnitPrice = paymentFromDb.Amount;
                                    subscriptionOrderLine.IsCustomPrice = true;

                                    subscriptionOrderLine.TotalPrice = Math.Round(subscriptionOrderLine.UnitPrice * subscriptionOrderLine.Quantity, 2);
                                }
                            }

                            break;

                    }

                    if (payOk)
                        ctx.Payment.Add(paymentFromDb);

                }
                else // payment update
                {
                    if (paymentFromDb.Amount != paymentToUpdate.Amount)
                    {
                        if (paymentToUpdate.PaymentType == PaymentType.Gift)
                        {
                            Gift gift = ctx.Gift.FirstOrDefault(g => g.Id == paymentToUpdate.GiftId);

                            // first we add the previous used value again to the gift
                            if (gift != null)
                                gift.AmountConsumed += paymentFromDb.Amount;

                            // now we deduct the new value to be payed
                            PayFromGift(paymentToUpdate, gift);
                        }
                    }
                }

                if (payOk)
                {
                    paymentFromDb.Amount = paymentToUpdate.Amount;
                    paymentFromDb.GiftId = paymentToUpdate.GiftId;
                    paymentFromDb.PaymentType = paymentToUpdate.PaymentType;
                    paymentFromDb.Info = paymentToUpdate.Info;
                    paymentFromDb.Date = paymentToUpdate.Date;


                }
            }

            if (orderPricingChanged)
                this.CalculateTotals(orderFromDb);

            UpdatePaymentSummary(orderFromDb);
        }

        /// <summary>
        /// When a new subscription payment is performed we need to locate the corresponding orderLine
        /// </summary>
        /// <param name="order"></param>
        /// <param name="subscription"></param>
        /// <returns></returns>
        private OrderLine GetOrderLineForSubscription(Order order, Subscription subscription)
        {
            if (order == null || order.OrderLines == null || order.OrderLines.Count == 0)
                return null;

            // if a template order => we also need to compare the product options !!


            OrderLine subscriptionOrderLine = null;

            Product subscriptionProduct = cache.GetProduct(subscription.SubscriptionProductId);
            //Product subscriptionUnitProduct = cache.GetProduct(subscription.SubscriptionUnitProductId);

            if (subscriptionProduct.TemplateOrderId == null)
            {
                subscriptionOrderLine = order.OrderLines.FirstOrDefault(ol => ol.ProductId == subscription.SubscriptionUnitProductId);
            }
            else  // subscription with a template order
            {
                Order templateOrder = this.GetOrder(subscriptionProduct.TemplateOrderId);

                if (templateOrder.OrderLines.Count() != 1)
                    throw new ApplicationException("Template order should have exactly 1 orderline!");

                OrderLine templateOrderLine = templateOrder.OrderLines.FirstOrDefault();

                // find the options that matter => we will need to find an orderline with the same configuration
                //List<OrderLineOption> templateOrderLineOptions = GetTemplateOrderLineOptions(templateOrderLine);

                ICollection<OrderLineOption> templateOrderLineOptions = templateOrderLine.OrderLineOptions;

                // find orderLine with the same options selected
                subscriptionOrderLine = GetOrderLineWithSameOptions(order.OrderLines, subscription.SubscriptionUnitProductId, templateOrderLineOptions);
            }

            return subscriptionOrderLine;
        }

        private OrderLine GetOrderLineWithSameOptions(ICollection<OrderLine> orderLines, string productId, ICollection<OrderLineOption> optionsToMatch)
        {
            if (orderLines == null)
                return null;

            List<OrderLine> orderLinesForProduct = orderLines.Where(ol => ol.ProductId == productId).ToList();

            if (orderLinesForProduct.Count == 0)
                return null;

            if (optionsToMatch == null || optionsToMatch.Count == 0)
                return orderLinesForProduct.FirstOrDefault();

            // we know we have to match at least 1 option
            foreach (OrderLine orderLine in orderLinesForProduct)
            {
                // if no options => no candidate
                if (orderLine.OrderLineOptions == null || orderLine.OrderLineOptions.Count == 0)
                    continue;

                bool isMatch = true;

                foreach (OrderLineOption optionToMatch in optionsToMatch)
                {
                    OrderLineOption option = orderLine.OrderLineOptions.FirstOrDefault(olo => olo.ProductOptionId == optionToMatch.ProductOptionId);

                    if (option == null)
                    {
                        isMatch = false;
                        break;
                    }
                    else if (!string.IsNullOrWhiteSpace(optionToMatch.ProductOptionValueId))
                    {
                        if (optionToMatch.ProductOptionValueId != option.ProductOptionValueId)
                        {
                            isMatch = false;
                            break;
                        }
                    }
                    else if (optionToMatch.ProductOptionValue != option.ProductOptionValue)
                    {
                        isMatch = false;
                        break;
                    }
                }

                if (isMatch)
                    return orderLine;
            }

            return null;
        }

        /// <summary>
        /// Not all orderline options need to be filled in for a template (only the ones that matter)
        /// => just return the ones that matter
        /// </summary>
        /// <param name="orderLine"></param>
        private List<OrderLineOption> GetTemplateOrderLineOptions(OrderLine orderLine)
        {
            List<OrderLineOption> options = new List<OrderLineOption>();

            if (orderLine == null || orderLine.OrderLineOptions == null || orderLine.OrderLineOptions.Count == 0)
                return options;

            foreach (OrderLineOption option in orderLine.OrderLineOptions)
            {
                if (option.ProductOptionValueId != null)
                    options.Add(option);
            }

            return options;

        }

        private void UpdateFulfillmentStatus(Order order)
        {
            if (order == null || order.OrderLines == null)
                return;

            if (order.IsAppointment)
            {
                order.FulfillmentStatus = (short)FulfillmentStatus.Accepted;
            }
            else  // normal product order
            {
                decimal totalQuantity = order.OrderLines.Sum(ol => ol.Quantity);
                decimal fulfilledQuantity = order.OrderLines.Sum(ol => ol.FulfilledQuantity);

                if (fulfilledQuantity == 0 && totalQuantity > 0)
                {
                    order.FulfillmentStatus = (short)FulfillmentStatus.NotFulfilled;
                }
                else if (fulfilledQuantity < totalQuantity)
                {
                    order.FulfillmentStatus = (short)FulfillmentStatus.PartiallyFulfilled;
                }
                else
                {
                    order.FulfillmentStatus = (short)FulfillmentStatus.Fulfilled;
                }

            }
        }

        public void UpdatePaymentSummary(Order order)
        {
            decimal totalPaid = 0;

            if (order.Payments != null)
            {
                foreach (var payment in order.Payments.Where(p => !p.IsDeleted && p.IsActive && p.PaymentType != PaymentType.AccountDeposit))
                {
                    totalPaid += payment.Amount;
                }
            }

            if (totalPaid == 0)
                order.PaymentStatus = (short)PaymentStatus.NotPaid;
            else if (totalPaid >= order.GrandTotal)
                order.PaymentStatus = (short)PaymentStatus.Paid;
            else if (order.Advance > 0 && totalPaid >= order.Advance)
            {
                order.PaymentStatus = (short)PaymentStatus.AdvancePaid;

            }
            else if (totalPaid > 0)
            {
                order.PaymentStatus = (short)PaymentStatus.PartiallyPaid;
            }
            else
                order.PaymentStatus = (short)PaymentStatus.Undefined;

            order.TotalPayed = totalPaid;
        }

        private decimal PayFromGift(Payment payment, Gift gift)
        {
            decimal possibleAmount = 0;

            if (gift != null)
            {
                decimal availableAmountOnGift = gift.Value - gift.AmountConsumed;

                possibleAmount = Math.Min(payment.Amount, availableAmountOnGift);  // paymentToUpdate.Amount

                payment.Amount = possibleAmount;
                payment.Info = gift.GiftCode;

                gift.AmountConsumed += possibleAmount;

                if (gift.AmountConsumed >= gift.Value)
                    gift.IsCompletelyConsumed = true;
            }
            else
            {
                payment.Amount = 0;
            }

            return possibleAmount;
        }

        private int CountProductsOfType(Order order, ProductType productType)
        {
            List<Product> products = cache.GetProducts(order.GetProductIds());

            if (products == null || products.Count == 0)
                return 0;

            return products.Count(p => p.ProductType == productType);
        }

        private List<Product> GetProductsOfType(Order order, ProductType productType)
        {
            List<Product> products = cache.GetProducts(order.GetProductIds());

            if (products == null)
                return new List<Product>();

            return products.Where(p => p.ProductType == productType).ToList();
        }

        private Subscription GetSubscriptionForOrderLine(OrderLine orderLine, List<Subscription> subscriptions)
        {
            Subscription subscription = subscriptions.FirstOrDefault(s => s.SubscriptionProductId == orderLine.ProductId);

            return subscription;
        }


        //Order orderToUpdate, Order orderFromDb,
        public bool ProcessSubscriptionOrder(Order orderToUpdate, Order orderFromDb, OpenBizContext ctx)
        {

            // the order has subscription orderlines => create or update (the price) of the subscriptions
            if (CountProductsOfType(orderFromDb, ProductType.Subscription) == 0)
                return false;

            List<Product> subscriptionProducts = GetProductsOfType(orderFromDb, ProductType.Subscription);
            //List<string> subscriptionProductIds = subscriptionProducts.Select(p => p.Id).ToList();

            decimal paidFactor = 1;

            if (orderFromDb.GrandTotal != 0)
                paidFactor = orderFromDb.TotalPayed / orderFromDb.GrandTotal;

            List<Subscription> existingSubscriptionsForOrder = ctx.Subscription.Where(s => s.IsActive && s.PurchaseOrderId == orderFromDb.Id)
                .OrderByDescending(s => s.UsedQuantity).ToList();


            foreach (Product subscriptionProduct in subscriptionProducts)
            {
                int nrOfExistingSubscriptionsForProduct = existingSubscriptionsForOrder.Count(s => s.SubscriptionProductId == subscriptionProduct.Id);
                int nrOfSubscriptions = 0;

                List<OrderLine> orderLinesForSubscription = orderFromDb.OrderLines.Where(ol => ol.ProductId == subscriptionProduct.Id).ToList();

                foreach (OrderLine orderLine in orderLinesForSubscription)
                {
                    for (int i = 0; i < (int)orderLine.Quantity; i++)
                    {
                        Subscription subscription = existingSubscriptionsForOrder.FirstOrDefault(s => s.SubscriptionProductId == subscriptionProduct.Id);

                        if (subscription != null)
                            existingSubscriptionsForOrder.Remove(subscription);

                        if (subscription == null)
                        {
                            subscription = Subscription.CreateNew();

                            subscription.PurchaseOrderId = orderFromDb.Id;
                            subscription.OrganisationId = orderFromDb.OrganisationId;
                            subscription.PartyId = orderFromDb.PartyId;
                            subscription.SubscriptionProductId = subscriptionProduct.Id;

                            subscription.Name = subscriptionProduct.Name;
                            subscription.SubscriptionUnitProductId = subscriptionProduct.SubscriptionUnitProductId;

                            subscription.TotalQuantity = subscriptionProduct.SubscriptionQuantity;
                            subscription.UsedQuantity = 0;

                            subscription.TotalPrice = subscriptionProduct.SalesPrice;

                            ctx.Subscription.Add(subscription);
                        }

                        subscription.TotalPayed = subscription.TotalPrice * paidFactor;

                        nrOfSubscriptions++;
                    }
                }

                if (nrOfSubscriptions < nrOfExistingSubscriptionsForProduct)
                {
                    /* there are more subscriptions for the orderLine then the qty (probably the qty was reduced
                    *   => inactivate these subscriptions
                    */

                    for (int i = nrOfSubscriptions; i < nrOfExistingSubscriptionsForProduct; i++)
                    {
                        Subscription subscription = existingSubscriptionsForOrder.FirstOrDefault(s => s.SubscriptionProductId == subscriptionProduct.Id);

                        if (subscription != null)
                        {
                            subscription.IsActive = false;
                            subscription.IsDeleted = true;
                        }
                    }
                }
            }

            ctx.SaveChanges();



            return true;
        }


        /// <summary>
        /// Extra logic for upload credit
        /// </summary>
        /// <param name="orderToUpdate"></param>
        /// <param name="orderFromDb"></param>
        /// <param name="ctx"></param>
        public bool ProcessCreditUploadOrder(Order orderToUpdate, Order orderFromDb, Dictionary<string, Product> productsDictionary, OpenBizContext ctx)
        {
            string orderId = orderFromDb.Id;

            if (orderToUpdate.IsCreditUploadOrder())
            {
                // we reload the payments: to be sure to have most correct view
                List<Payment> payments = ctx.Payment.Where(p => p.IsActive && p.OrderId == orderId).ToList();

                decimal totalAlreadyLoaded = payments.Where(p => p.PaymentType == PaymentType.AccountDeposit).Select(p => p.Amount).Sum();

                decimal totalToLoad = payments.Where(p => p.PaymentType != PaymentType.AccountDeposit).Select(p => p.Amount).Sum();

                decimal depositAmount = totalToLoad - totalAlreadyLoaded;
                if (depositAmount != 0)
                {
                    FinAccount finAccount = GetOrCreateAccount(orderToUpdate.OrganisationId, orderToUpdate.PartyId, ctx);

                    Payment accountDeposit = Payment.CreateNew();
                    accountDeposit.OrderId = orderId;
                    accountDeposit.Amount = depositAmount;
                    accountDeposit.PaymentType = PaymentType.AccountDeposit;
                    accountDeposit.FinAccountId = finAccount.Id;
                    ctx.Payment.Add(accountDeposit);

                    finAccount.Amount += depositAmount;

                    OrderLine orderLine = orderFromDb.OrderLines.FirstOrDefault();

                    if (!productsDictionary.ContainsKey(orderLine.ProductId))
                        return true;

                    Product product = productsDictionary[orderLine.ProductId];

                    if (product != null && !string.IsNullOrWhiteSpace(product.SystemTag))
                    {
                        if (product.SystemTag.ToLower().Contains("loadcredit"))
                        {
                            orderLine.UnitPrice = totalToLoad;
                            orderLine.IsCustomPrice = true;
                        }
                    }

                    SetOrderLinePrices(orderFromDb, ctx);
                    CalculateTotals(orderFromDb);
                    UpdatePaymentSummary(orderFromDb);

                    ctx.SaveChanges();

                    return true;
                }
            }

            return false;
        }


        public FinAccount GetOrCreateAccount(string orgId, string partyId, OpenBizContext ctx)
        {
            FinAccount finAccount = ctx.FinAccount.FirstOrDefault(a => a.PartyId == partyId && (a.Name == null || a.Name.Trim() == ""));

            if (finAccount == null)
            {
                finAccount = FinAccount.CreateNew();

                finAccount.OrganisationId = orgId;
                finAccount.PartyId = partyId;

                finAccount.Amount = 0;

                ctx.FinAccount.Add(finAccount);
            }



            return finAccount;
        }

        public void Log(DbEntityValidationException ex, Logger logger)
        {
            foreach (var dbError in ex.EntityValidationErrors)
            {
                foreach (var error in dbError.ValidationErrors)
                {
                    string message = string.Format("Error: {0} {1} {2}", dbError.Entry.Entity.ToString(), error.PropertyName, error.ErrorMessage);
                    logger.Error(message);
                    Console.Out.WriteLine(message);
                }
            }

            _Logger.Error(ex.ToString());
        }

        public void DeleteOrder(string orderId)
        {
            OpenBizContext ctx = new OpenBizContext();

            /*
            Order order = new Order { Id = orderId };
            ctx.Entry(order).State = EntityState.Deleted;
            ctx.SaveChanges();
            */

            Order order = ctx.Order.Include("Payments").Include("Appointments.ResourcePlannings").Include("OrderLines.OrderLineOptions").FirstOrDefault(o => o.Id == orderId);

            if (order != null)
            {
                if (order.Payments != null)
                {
                    foreach (var pay in order.Payments.ToList())
                    {
                        ctx.Payment.Remove(pay);
                    }
                }

                if (order.Appointments != null)
                {
                    foreach (var app in order.Appointments.ToList())
                    {
                        if (app.ResourcePlannings != null)
                        {
                            foreach (var rp in app.ResourcePlannings.ToList())
                            {
                                ctx.ResourcePlanning.Remove(rp);
                            }
                        }

                        ctx.Appointment.Remove(app);
                    }
                }

                if (order.OrderLines != null)
                {
                    foreach (var orderLine in order.OrderLines.ToList())
                    {
                        if (orderLine.OrderLineOptions != null)
                        {
                            foreach (var olOption in orderLine.OrderLineOptions.ToList())
                            {
                                ctx.OrderLineOption.Remove(olOption);
                            }
                        }

                        ctx.OrderLine.Remove(orderLine);
                    }
                }
                ctx.SaveChanges();

                ctx.Order.Remove(order);

                ctx.SaveChanges();
            }
        }

        public OrderCancelResult CancelOrder(string orderId)
        {
            OpenBizContext ctx = new OpenBizContext();

            Order order = ctx.Order.Include("Appointments").FirstOrDefault(o => o.Id == orderId);

            if (order == null)
            {
                return new OrderCancelResult(OrderCancelResultStatus.OrderNotFound);
            }

            order.IsDeleted = true;
            order.IsActive = false;
            order.DeletedAt = DateTime.Now;

            if (order.IsAppointment || (order.Appointments != null && order.Appointments.Count() > 0))
            {
                foreach (Appointment app in order.Appointments)
                {

                    ctx.Entry(app).Collection(a => a.ResourcePlannings).Load();

                    app.ResourcePlannings.ToList().ForEach(rp => ctx.ResourcePlanning.Remove(rp));

                    app.IsActive = false;
                    app.IsDeleted = true;
                }
                /*
                if (order.Appointments != null)
                {
                    foreach (var app in order.Appointments)
                    {
                        app.Delete();
                    }
                }*/
            }

            ctx.SaveChanges();

            return new OrderCancelResult(OrderCancelResultStatus.OK);
        }

        public void SetOrderInfo(Order order)
        {

        }

        public void SetAppointmentInfo(Appointment app)
        {
            if (app == null)
                return;



            app.Title = string.Format("{0:HH:mm}-{1:HH:mm}", app.Start, app.End);

            StringBuilder sb = new StringBuilder();

            if (app.Order != null)
            {
                Order order = app.Order;

                if (order.OrderLines != null)
                {
                    //Dictionary<string, Product> products = cache.GetProductsAsDictionary(order.OrderLines.Select(ol => ol.ProductId));

                    foreach (OrderLine ol in order.OrderLines)
                    {
                        sb.AppendFormat("{0:0}x{1}", ol.Quantity, ol.Description).AppendLine();
                        /*
                        Product prod = null;

                        if (products.ContainsKey(ol.ProductId))
                            prod = products[ol.ProductId];

                        if (prod != null)
                            sb.AppendFormat("{0}x{1}", ol.Quantity, prod.Name).AppendLine();
                         */
                    }
                }
            }

            app.Description = sb.ToString();
        }
    }
}
