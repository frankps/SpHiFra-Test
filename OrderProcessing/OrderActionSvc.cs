using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Model = Dvit.OpenBiz.Model;
using Dvit.OpenBiz.Model;
using Dvit.OpenBiz.Database;
using Dvit.OpenBiz.Pcl;
using Dvit.OpenBiz.Reservation;
using NLogLight;
using Dvit.OpenBiz.Services.Data;
using Dvit.OpenBiz.Services.Communication;
using Dvit.OpenBiz.Services;
using Newtonsoft.Json;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;

namespace Dvit.OpenBiz.Services.OrderProcessing
{
    public class OrderActionRequest
    {
        public string OrderId { get; set; }

        public string Action { get; set; }
    }

    public enum OrderActionResponseStatus
    {
        Undefined,
        Ok,
        ActionNotDefined = -1,
        OrderIdNotDefined = -2,
        ExceptionOccurred = -3,
        MissingRelatedEntity = -4,
        MissingData = -5
    }

    public class OrderActionResponse
    {
        [JsonProperty(PropertyName = "status")]
        public OrderActionResponseStatus Status { get; set; }

        [JsonProperty(PropertyName = "message")]
        public string Message { get; set; }

        public OrderActionResponse(OrderActionResponseStatus status)
        {
            this.Status = status;
        }

        public OrderActionResponse(OrderActionResponseStatus status, string message)
        {
            this.Message = message;
        }
    }

    public class OrderActionSvc : OpenBizSvc
    {
        public async Task<OrderActionResponse> ExecuteAction(OrderActionRequest request)
        {
            try
            {
                this.TrackTrace("OrderActionSvc.ExecuteAction(...) triggered", request);

                if (request == null || string.IsNullOrWhiteSpace(request.Action))
                {
                    return new OrderActionResponse(OrderActionResponseStatus.ActionNotDefined);
                }

                if (string.IsNullOrWhiteSpace(request.OrderId))
                {
                    return new OrderActionResponse(OrderActionResponseStatus.OrderIdNotDefined);
                }

                Order order = this.Ctx.Order.FirstOrDefault(o => o.Id == request.OrderId);

                BirdyMessagingSvc msgSvc = new BirdyMessagingSvc(this.Ctx);
                JobSvc jobSvc = new JobSvc();

                switch (request.Action.Trim())
                {
                    case "confirmAndWaitForBankTransfer":

                        order.OrderStatus = OrderStatus.Confirmed;
                        order.PaymentStatus = (short)PaymentStatus.WaitingForPayment;
                        this.Ctx.SaveChanges();

                        CreatePrivatePartyAndLinkToOrderIfNeeded(order);

                        await msgSvc.ExecuteMessage(BirdyTemplateCode.C_ConfirmReservation, null, OpenBizObjectType.Order, request.OrderId);
                        
                        await jobSvc.QueueJob(JobType.SendOrderDebugInfo, 
                            new SendOrderDebugInfoRequest() { OrderId = order.Id,
                                SubjectPrefix = "Birdy order action 'confirmAndWaitForBankTransfer'"
                            });

                        this.TrackTrace("OrderActionSvc.ExecuteAction() successfully executed!", request);

                        return GetBankTransferStructuredMessage(order);
                        

                    case "confirm":

                        order.OrderStatus = OrderStatus.Confirmed;
                        this.Ctx.SaveChanges();

                        CreatePrivatePartyAndLinkToOrderIfNeeded(order);

                        await msgSvc.ExecuteMessage(BirdyTemplateCode.C_ConfirmReservation, null, OpenBizObjectType.Order, request.OrderId);
                    
                        await jobSvc.QueueJob(JobType.SendOrderDebugInfo, 
                            new SendOrderDebugInfoRequest() { OrderId = order.Id,
                                SubjectPrefix = "Birdy order action 'confirm'"
                            });

                        this.TrackTrace("OrderActionSvc.ExecuteAction() successfully executed!", request);

                        break;

                    case "getAdvanceMessage":

                        return GetBankTransferStructuredMessage(order);
                        
                    default:

                        this.TrackError(string.Format("Action '{0}' not recognised!", request.Action), request);
                        break;
                }

                return new OrderActionResponse(OrderActionResponseStatus.Ok);
            }
            catch (Exception ex)
            {
                this.Log.TrackException(ex);

                return new OrderActionResponse(OrderActionResponseStatus.ExceptionOccurred);
            }
        }
        private async void CreatePrivatePartyAndLinkToOrderIfNeeded(Order order)
        {
            this.TrackTrace("OrderActionSvc.CreatePrivatePartyIfNeeded() start executing");

            if (order == null)
                return;
            this.TrackTrace("OrderActionSvc.CreatePrivatePartyIfNeeded() order not null, ok", order);

            //if order is coupled to public party: create a private party for this party to this organisation
            Party party = this.Ctx.Party.FirstOrDefault(p => p.Id == order.PartyId);
            if(party != null && party.IsPublic)
            {
                this.TrackTrace("OrderActionSvc.CreatePrivatePartyIfNeeded(), party is public", party);
                LinkOrganisationToPartyRequest request = new LinkOrganisationToPartyRequest();
                request.OrganisationId = order.OrganisationId;
                request.PartyId = order.PartyId;
                request.AllowOrgPushNotifications = true;

                this.TrackTrace("OrderActionSvc.CreatePrivatePartyIfNeeded(), partySvc.LinkOrganisationToParty is executed", request);
                PartySvc partySvc = new PartySvc(this.Ctx);
                LinkOrganisationToPartyResponse res = await partySvc.LinkOrganisationToParty(request);
                this.TrackTrace("OrderActionSvc.CreatePrivatePartyIfNeeded(), partySvc.LinkOrganisationToParty came back with status", res);

                //after the link, redirect the order to this new private party
                if(res.Status == LinkOrganisationToPartyResponseStatus.OK || res.Status == LinkOrganisationToPartyResponseStatus.AlreadyLinked)
                {
                    this.TrackTrace("OrderActionSvc.CreatePrivatePartyIfNeeded(), order is redirected to private party");
                    order.PartyId = res.PrivatePartyId;
                    this.Ctx.SaveChanges();
                }
            }
        }

        public OrderActionResponse GetBankTransferStructuredMessage(Order order)
        {
            Ctx.Entry(order).Reference(o => o.Organisation).Load();
            Ctx.Entry(order).Reference(o => o.Party).Load();

            if (order.Organisation == null)
                return new OrderActionResponse(OrderActionResponseStatus.MissingRelatedEntity, "order.Organisation not available");

            if (order.Party == null)
                return new OrderActionResponse(OrderActionResponseStatus.MissingRelatedEntity, "order.Party not available");

            if (string.IsNullOrWhiteSpace(order.Organisation.AdvanceMessage))
            {
                return new OrderActionResponse(OrderActionResponseStatus.MissingData,
                    string.Format("Organisation.AdvanceMessage not filled in for {0}", order.Organisation.Name));
            }

            string advanceMessage = order.Organisation.AdvanceMessage;
            advanceMessage = advanceMessage.Replace("$amount$", order.Advance.ToString("0"));

            string structMsg = string.Format("{0:yyyyMMddHHmm} {1}", order.Date.Value, order.Party.Name);
            advanceMessage = advanceMessage.Replace("$structuredMessage$", structMsg);

            return new OrderActionResponse(OrderActionResponseStatus.Ok, advanceMessage);
        }

    }
}

