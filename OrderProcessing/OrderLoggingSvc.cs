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
using Dvit.OpenBiz.Services.Communication;
using Dvit.OpenBiz.Services.PaymentGateway;
using Newtonsoft.Json;

namespace Dvit.OpenBiz.Services.OrderProcessing
{
    public class SendOrderDebugInfoRequest
    {
        public string SubjectPrefix { get; set; }
        public string OrderId { get; set; }
    }

    public class OrderLoggingSvc : OpenBizSvc
    {
        /*        public async Task ExecuteJob(Job job)
        {
            BirdyTriggerMessageRequest request = JsonConvert.DeserializeObject<BirdyTriggerMessageRequest>(job.Data);

            await this.ExecuteMessage(request);

        }*/
        public async Task SendOrderDebugInfo(Job job)
        {
            SendOrderDebugInfoRequest request = JsonConvert.DeserializeObject<SendOrderDebugInfoRequest>(job.Data);
            await this.SendOrderDebugInfo(request);
        }

        public async Task SendOrderDebugInfo(SendOrderDebugInfoRequest request)
        {
            var order = this.GetOrder(request.OrderId);

            var orderLog = order.ToLogNode();
            
            this.GetExtraOrderInfo(request.OrderId, orderLog);
            
            string html = orderLog.ToHtml();

            SendGridSvc sendGrid = new SendGridSvc();

            string subject = "";

            if (!string.IsNullOrWhiteSpace(request.SubjectPrefix))
                subject += request.SubjectPrefix + ":";
            else
                subject += "Birdy order log:";

            if (order.Party != null)
                subject += " " + order.Party.Name;

            if (order.Date.HasValue)
                subject += " " + order.Date.Value.ToString("dd/MM HH:mm");

            await sendGrid.Deliver("frank@dvit.eu", "hilde@aquasense.be,katrien@aquasense.be,frank@dvit.eu,info@aquasense.be",
                subject, html);
            
        }

        public Order GetOrder(string orderId, OpenBizContext ctx = null)
        {
            if (ctx == null)
                ctx = new OpenBizContext();

            Order order = ctx.Order.Include("OrderLines.OrderLineOptions.ProductOption")
                .Include("OrderLines.OrderLineOptions.ProductOptionValueObject")
                .Include("OrderLines.ResourcePlannings")
                .Include("OrderLines.Product")
                .Include("OrderTaxes").FirstOrDefault(o => o.Id == orderId);

            // .Include("OrderLines.ResourcePlannings")

            if (order != null)
            {
                ctx.Entry(order).Collection(o => o.Payments).Load();
                ctx.Entry(order).Collection(o => o.Appointments).Load();
                ctx.Entry(order).Reference("Party").Load();

                if (order.Appointments != null)
                {
                    foreach (var app in order.Appointments)
                    {
                        ctx.Entry(app).Collection(a => a.ResourcePlannings).Load();
                    }
                }
            }
            return order;
        }

        public void GetExtraOrderInfo(string orderId, LogNode parentNode = null)
        {
            LogNode onlinePaysLog = null;

            if (parentNode != null)
                onlinePaysLog = parentNode.AddSubNode("Online payments:");
            else
                onlinePaysLog = LogNode.Create("Online payments:");

            List<OnlinePayment> onlinePays = this.Ctx.OnlinePayment.Where(op => op.ObjectId == orderId).ToList();

            foreach (OnlinePayment olPay in onlinePays)
            {
                LogNode olPayLog = onlinePaysLog.AddSubNode("Online payment € {0}", olPay.Amount);
                olPayLog.AddItem("Created at: {0} - Updated at: {1}", olPay.CreatedAt.ToString(), olPay.UpdatedAt.ToString());

                if (!string.IsNullOrWhiteSpace(olPay.FinalAutoResponse))
                {
                    LogNode finalAutoResponseLog = olPayLog.AddSubNode("FinalAutoResponse");

                    WorldlinePayment worldline = new WorldlinePayment(WorldlinePaymentType.Acquirer, olPay.FinalAutoResponse);

                    //string codeInfo = worldline.ResponseCodeInfo;


                    finalAutoResponseLog.AddItem("Response code: {0} = {1}", worldline.ResponseCode, worldline.ResponseCodeInfo);
                    finalAutoResponseLog.AddItem("TransactionDateTime: {0}", worldline.TransactionDateTime);
                    finalAutoResponseLog.AddItem("PaymentMeanBrand: {0}", worldline.PaymentMeanBrand);

                }
                else
                {
                    olPayLog.AddSubNode("No autoresponse available!");
                }
            }

            /*
            LogNode orderLog = LogNode.Create("Order id='{0}'", this.Id);

            LogNode orderLog = LogNode.Create("Info about order");
            

            OrderSvc orderSvc = new OrderSvc();

            Order order = orderSvc.GetOrder(orderId, this.Ctx);

            if (order.OrderLines != null)
            {
                foreach (var orderLine in order.OrderLines)
                {
                    orderLog.AddSubNode("{0} x {1}", orderLine.Quantity, orderLine.Description);
                }
            }
            */

    }

}
}
