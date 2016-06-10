using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dvit.OpenBiz.Services.Data;
using Dvit.OpenBiz.Model;

namespace Dvit.OpenBiz.Services.OrderProcessing
{
    public class OrderCleanupSvc : OpenBizSvc
    {
        /// <summary>
        /// Consumers can make orders
        /// </summary>
        public async Task DeleteTemporaryOrders()
        {
            try
            {
                int orderTimeOutMinutes = 15;

                string subjectPrefix = string.Format("Birdy order timeout ({0}min)", orderTimeOutMinutes);

                JobSvc jobSvc = new JobSvc(this.Ctx);


                DateTimeOffset now = DateTimeOffset.Now;
                DateTimeOffset deleteBefore = now.AddMinutes(-orderTimeOutMinutes);

                string message = string.Format("Check for inprogress orders created before {0:HH}h{0:mm}({0:ss})", deleteBefore);
                TrackTrace(message);

                int batchSize = 20, maxBatchNum = 100;

                for (int batchNum = 1; maxBatchNum <= 0 || batchNum <= maxBatchNum; batchNum++)
                {
                    StringBuilder sbLog = new StringBuilder();

                    List<Order> orders = this.Ctx.Order.Include("Party").Include("OrderLines.ResourcePlannings")
                    .Include("Appointments")
                    .Where(o => o.IsActive && o.CreatedAt < deleteBefore
                && (o.OrderStatus & OrderStatus.InProgress) == OrderStatus.InProgress)
                .OrderBy(o => o.CreatedAt).Skip(batchSize * (batchNum - 1)).Take(batchSize).ToList();

                    if (orders == null || orders.Count == 0)
                    {
                        TrackTrace("No orders to inactivate!");
                        return;
                    }

                    TrackTrace(string.Format("{0} orders in progress older then {1} minutes: these will be placed on inactive (batch:{2})!", orders.Count, orderTimeOutMinutes, batchNum));

                    foreach (Order order in orders)
                    {
                        sbLog.AppendFormat("  Order {0:dd/MM/yy HHhmm} {1}", order.Date, order.Party != null ? order.Party.Name : "?").AppendLine();

                        if (order.OrderLines != null)
                        {
                            foreach (OrderLine orderLine in order.OrderLines)
                            {
                                if (orderLine.ResourcePlannings != null)
                                {
                                    this.Ctx.ResourcePlanning.RemoveRange(orderLine.ResourcePlannings);

                                    /*
                                    foreach (ResourcePlanning planning in orderLine.ResourcePlannings.ToList())
                                    {
                                        planning.IsActive = false;
                                        planning.IsDeleted = true; 

                                        this.Ctx.ResourcePlanning.Remove(planning);
                                    } */

                                }

                                sbLog.AppendFormat("      {0}", orderLine.Description).AppendLine();
                            }
                        }

                        if (order.Appointments != null)
                        {
                            this.Ctx.Appointment.RemoveRange(order.Appointments);
                            /*
                            foreach (Appointment app in order.Appointments)
                            {
                                app.IsActive = false;
                                app.IsDeleted = true;
                            } */
                        }

                        order.IsActive = false;
                        order.IsDeleted = true;

                        order.OrderStatus = order.OrderStatus & ~OrderStatus.InProgress;
                        order.OrderStatus = order.OrderStatus | OrderStatus.TimedOut;

                        await jobSvc.QueueJob(JobType.SendOrderDebugInfo,
                            new SendOrderDebugInfoRequest()
                            {
                                OrderId = order.Id,
                                SubjectPrefix = subjectPrefix
                            });


                        this.Ctx.SaveChanges();
                    }

                    TrackTrace(sbLog.ToString());
                }
            }
            catch (Exception ex)
            {
                this.Log.TrackException(ex);
            }


        }
    }
}
