using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dvit.OpenBiz.Services.Data;
using Dvit.OpenBiz.Model;
using Dvit.OpenBiz.Database;
using Dvit.OpenBiz.Pcl;
using Newtonsoft.Json;

namespace Dvit.OpenBiz.Services.OrderProcessing
{
    [Flags]
    public enum OrderDateFilter
    {
        NoDateFilter = 0,
        CreatedAt = 1,
        Date = 2
    }

    [Flags]
    public enum OrderCustomFilter
    {
        NoCustomFilter = 0,
        Appointment = 1,
        Purchase = 2,
        IsGift = 4,
        InvoiceNeeded = 8,
    }

    [Flags]
    public enum OrderPaymentFilter
    {
        NoPaymentFilter = 0,
        NotPaid = 1,
        PartialPaid = 2,
        TotalPaid = 4
    }

    public class OrderSearchRequest
    {
        public string OrganisationId { get; set; }
        public string What { get; set; }
        public OrderDateFilter DateFilter { get; set; }
        public DateTimeOffset From { get; set; }
        public DateTimeOffset To { get; set; }
        public bool? IsActive { get; set; }
        public OrderCustomFilter CustomFilter { get; set; }
        public OrderPaymentFilter PaymentFilter { get; set; }
        public int Skip { get; set; }   //pageNumber
        public int Take { get; set; }   //pageSize
    }

    public class OrderSearchResponse
    {
        [JsonProperty(PropertyName = "results")]
        public List<OrderDto> Results { get; set; }

        [JsonProperty(PropertyName = "count")]
        public int Count { get; set; }

        public OrderSearchResponse(List<OrderDto> orders)
        {
            this.Results = orders;
            if (orders != null)
            {
                this.Count = 100;
                int count = orders.Count();
            }
        }

        public OrderSearchResponse(List<OrderDto> orders, int count)
        {
            this.Results = orders;

            this.Count = count;
        }
    }

    public class OrderDto
    {
        public string PartyName { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTime? Date { get; set; }

        public string Description { get; set; }

        public string OrderId { get; set; }

        public decimal OrderGrandTotal { get; set; }

        public decimal OrderTotalPayed { get; set; }

        public Int16 OrderPaymentStatus { get; set; }

        public bool OrderInvoiceNeeded { get; set; }

        public string OrderInvoiceNr { get; set; }

        public bool OrderIsGift { get; set; }

        public string OrderGiftCode { get; set; }

        public OrderType OrderType { get; set; }

        public Int16 OrderFulfillmentStatus { get; set; }

        public bool OrderIsActive { get; set; }

        public bool OrderIsAppointment { get; set; }

        public string OrderRemark { get; set; }

        public OrderStatus OrderStatus { get; set; }
    }

    public class OrderSearchSvc : OpenBizSvc
    {
        public OrderSearchSvc() { }
        public OrderSearchSvc(OpenBizContext ctx) : base(ctx) { }

        public OrderSearchResponse Search(OrderSearchRequest request)
        {
            int skip = request.Skip; //* request.Take;

            if (string.IsNullOrWhiteSpace(request.What))
                request.What = null;


            var query = this.Ctx.Order.Where(o => o.OrganisationId == request.OrganisationId
            && (request.DateFilter == OrderDateFilter.NoDateFilter
                || ((request.DateFilter & OrderDateFilter.CreatedAt) == OrderDateFilter.CreatedAt && o.CreatedAt >= request.From && o.CreatedAt <= request.To)
                || ((request.DateFilter & OrderDateFilter.Date) == OrderDateFilter.Date && o.Date >= request.From && o.Date <= request.To)
            )
            && (request.What == null
                || o.Party.Name.Contains(request.What)
                || o.GiftCode.Contains(request.What)
                || o.InvoiceNumber.Contains(request.What))
            && (request.IsActive == null || o.IsActive == request.IsActive)
            && (request.CustomFilter == OrderCustomFilter.NoCustomFilter
                || ((request.CustomFilter & OrderCustomFilter.Appointment) == OrderCustomFilter.Appointment && o.IsAppointment)
                || ((request.CustomFilter & OrderCustomFilter.Purchase) == OrderCustomFilter.Purchase && !o.IsAppointment)
                || ((request.CustomFilter & OrderCustomFilter.IsGift) == OrderCustomFilter.IsGift && o.IsGift)
                || ((request.CustomFilter & OrderCustomFilter.InvoiceNeeded) == OrderCustomFilter.InvoiceNeeded && o.InvoiceNeeded)
            )
            && (request.PaymentFilter == OrderPaymentFilter.NoPaymentFilter
                || (((request.PaymentFilter & OrderPaymentFilter.NotPaid) == OrderPaymentFilter.NotPaid) && o.TotalPayed == 0)
                || (((request.PaymentFilter & OrderPaymentFilter.PartialPaid) == OrderPaymentFilter.PartialPaid) && o.TotalPayed>0 && o.TotalPayed<o.GrandTotal)
                || (((request.PaymentFilter & OrderPaymentFilter.TotalPaid) == OrderPaymentFilter.TotalPaid) && o.TotalPayed==o.GrandTotal)
            )
            );

            var total = query.Count();

            List<OrderDto> orders = query.OrderBy(o => o.CreatedAt)
            .Select(o => new OrderDto()
            {
                PartyName = o.Party != null ? o.Party.Name : null,
                Date = o.Date,
                CreatedAt = o.CreatedAt,
                Description = o.Description,
                OrderId = o.Id,
                OrderGrandTotal = o.GrandTotal,
                OrderTotalPayed = o.TotalPayed,
                OrderPaymentStatus = o.PaymentStatus,
                OrderInvoiceNeeded = o.InvoiceNeeded,
                OrderInvoiceNr = o.InvoiceNumber,
                OrderIsGift = o.IsGift,
                OrderGiftCode = o.GiftCode,
                OrderType = o.OrderType,
                OrderFulfillmentStatus = o.FulfillmentStatus,
                OrderIsActive = o.IsActive,
                OrderIsAppointment = o.IsAppointment,
                OrderRemark = o.Remark,
                OrderStatus = o.OrderStatus
            }).Skip(skip).Take(request.Take).ToList();
            

            return new OrderSearchResponse(orders, total);
        }

    }
}
