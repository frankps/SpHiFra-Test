using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dvit.OpenBiz.Database;
using Dvit.OpenBiz.Model;
using Dvit.OpenBiz.Pcl;

namespace Dvit.OpenBiz.Services.OrderProcessing
{
    public class OrderParser
    {
        OpenBizContext _Ctx;

        public Organisation Organisation { get; set; }
        public List<Catalog> Catalogs { get; set; }
        public Account Account { get; set; }
        public Party Party { get; set; }

        public OrderParser(string organisationName, string accountLoginName = null, string partyName = null)
        {
            _Ctx = new OpenBizContext();

            Organisation = _Ctx.Organisation.FirstOrDefault(o => o.UniqueName == organisationName);

            Catalogs = _Ctx.Catalog.Where(c => c.OrganisationId == this.Organisation.Id).ToList();

            if (!string.IsNullOrWhiteSpace(accountLoginName))
                Account = _Ctx.Account.FirstOrDefault(a => a.LoginName == accountLoginName);

            if (Account == null)
                Account = _Ctx.Account.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(partyName))
                Party = _Ctx.Party.FirstOrDefault(a => a.Name == partyName);

            if (Party == null)
                Party = _Ctx.Party.FirstOrDefault();

            /*
            _Account = ctx.Account.FirstOrDefault();
        else
            _Account = ctx.Account.FirstOrDefault(a => a.LoginName == _AccountLoginName);
            */
        }

        public ParseOrderResult ParseOrder(string orderAsString)
        {
            if (Organisation == null)
            {
                return new ParseOrderResult(ParseOrderResultState.OrganisationNotFound);
            }

            ParseOrderResultState state = ParseOrderResultState.OK;


            List<string> catalogIds = Catalogs.Select(c => c.Id).ToList();

            List<string> orderLineArray = orderAsString.Split(',').ToList();

            Order order = Order.CreateNew();
            //new Order()
            /*
            {
                OrganisationId = this.Organisation.Id
            };*/

            order.OrganisationId = this.Organisation.Id;

            if (this.Account != null)
            {
                order.Account = this.Account;
                order.AccountId = this.Account.Id;
            }

            if (this.Party != null)
            {
                order.Party = this.Party;
                order.PartyId = this.Party.Id;
            }

            order.OrderType = OrderType.SalesOrder;
            order.OrderLines = new List<OrderLine>();

            List<ParseOrderLineResult> orderLineResults = new List<ParseOrderLineResult>();

            PersonList personList = new PersonList();

            int lineNumber = 10;
            orderLineArray.ForEach(ol =>
            {
                ParseOrderLineResult orderLineResult = ParseOrderLine(this.Organisation, catalogIds, ol.Trim());
                orderLineResults.Add(orderLineResult);

                if (orderLineResult.State == ParseOrderLineResultState.OK)
                {
                    if (lineNumber == 10)
                    {
                        //order.TaxIncluded = orderLineResult
                    }
                    var orderLine = orderLineResult.OrderLine;

                    if (!string.IsNullOrWhiteSpace(orderLine.Persons))
                    {
                        PersonCodes codes = new PersonCodes(orderLine.Persons);

                        foreach (string code in codes.Codes)
                        {
                            if (!personList.Contains(code))
                                personList.AddPerson(new Person(code, code));
                        }
                    }

                    orderLine.Order = order;
                    orderLine.LineNumber = lineNumber;
                    order.OrderLines.Add(orderLine);

                    if (orderLine.Product.NeedsPlanning)
                        order.IsAppointment = true;

                    lineNumber += 10;
                }
                else
                    state = ParseOrderResultState.AtLeastOneProblem;
            });

            order.Persons = personList.ToString();
            order.NrOfPersons = personList.Persons.Count;

            ParseOrderResult result = new ParseOrderResult(order, state);
            result.OrderLineResults = orderLineResults;

            return result;
        }

        private ParseOrderLineResult ParseOrderLine(Organisation org, List<string> catalogIds, string orderLinesAsString)
        {
            var olElements = orderLinesAsString.Split(new string[] { " x " }, StringSplitOptions.None).ToList();

            if (olElements.Count != 2)
            {
                return new ParseOrderLineResult(ParseOrderLineResultState.ParseError);
            }

            string quantityString = olElements[0].Trim();

            decimal quantity = 1;

            if (!decimal.TryParse(quantityString, out quantity))
            {
                return new ParseOrderLineResult(ParseOrderLineResultState.ParseError);
            }



            string productName = olElements[1].Trim();
            bool productHasOptions = false;
            string[] productOptionElements = null;

            if (productName.Contains('?'))
            {
                var productNameElements = productName.Split('?').ToList();

                productName = productNameElements[0].Trim();

                productHasOptions = true;
                productOptionElements = productNameElements[1].Split('&');
            }

            Product product = _Ctx.Product.FirstOrDefault(p => p.Name == productName && catalogIds.Contains(p.CatalogId));

            if (product == null)
            {
                string msg = string.Format("Product '{0}' not found for line: {1}", productName, orderLinesAsString);
                return new ParseOrderLineResult(ParseOrderLineResultState.ProductNotFound, msg);
            }

            _Ctx.Entry(product).State = System.Data.Entity.EntityState.Detached;

            OrderLine orderLine = new OrderLine()
            {
                Quantity = quantity,
                ProductId = product.Id,
                Product = product,
                PlanningOrder = product.PlanningOrder
            };

            if (productHasOptions)
            {
                orderLine.OrderLineOptions = new List<OrderLineOption>();

                for (int i = 0; i < productOptionElements.Length; i++)
                {
                    string optionString = productOptionElements[i].Trim();
                    var optionElements = optionString.Split('=');

                    string optionCode = optionElements[0].Trim();
                    string optionValue = optionElements[1].Trim();

                    if (optionCode.ToLower() == "persons")
                    {
                        orderLine.Persons = optionValue;
                        continue;
                    }

                    ProductOption option = _Ctx.ProductOption.FirstOrDefault(po => po.Code == optionCode && po.OrganisationId == org.Id);

                    if (option == null)
                    {
                        string msg = string.Format("Product option '{0}' not found for line: {1}", optionCode, orderLinesAsString);
                        return new ParseOrderLineResult(ParseOrderLineResultState.ProductOptionNotFound, msg);
                    }

                    /*
                    OrderLineOption olOption = new OrderLineOption()
                    {
                        ProductOptionId = option.Id
                    };*/

                    OrderLineOption olOption = OrderLineOption.CreateNew();
                    olOption.ProductOptionId = option.Id;

                    if (option.HasValueObjects)
                    {
                        ProductOptionValue value = _Ctx.ProductOptionValue.FirstOrDefault(v => v.ProductOptionId == option.Id && v.Code == optionValue);

                        if (value != null)
                        {
                            olOption.ProductOptionValueId = value.Id;
                        }
                        else
                        {
                            throw new ApplicationException(string.Format("Product option value not found: {0}!", optionValue));
                        }
                    }
                    else
                    {
                        decimal optionNumValue = 0;

                        if (!decimal.TryParse(optionValue, out optionNumValue))
                        {
                            string msg = string.Format("Could not parse value '{0}' for option '{1}' for line: {2}", optionValue, optionCode, orderLinesAsString);
                            return new ParseOrderLineResult(ParseOrderLineResultState.InvalidProductOptionValue);
                        }

                        olOption.ProductOptionValue = optionNumValue;
                    }

                    orderLine.OrderLineOptions.Add(olOption);


                }
            }

            return new ParseOrderLineResult(orderLine);
        }
    }

    public class ParseOrderResult
    {
        public ParseOrderResult()
        {

        }

        public ParseOrderResult(ParseOrderResultState state)
        {

            State = state;
        }

        public ParseOrderResult(Order order, ParseOrderResultState state = ParseOrderResultState.OK)
        {
            Order = order;
            State = state;
        }

        public ParseOrderResultState State { get; set; }
        public Order Order { get; set; }
        public List<ParseOrderLineResult> OrderLineResults { get; set; }

        public string GetReport()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendFormat("Order parsing result: {0}", this.State).AppendLine().AppendLine(); ;

            sb.AppendLine("Order line problems:");

            if (OrderLineResults != null && OrderLineResults.Count > 0)
            {
                OrderLineResults.Where(ol => ol.State != ParseOrderLineResultState.OK).ToList().ForEach(ol =>
                {
                    sb.AppendFormat("  {0}: {1}", ol.State, ol.Message).AppendLine();
                });
            }


            return sb.ToString();
        }

    }

    public enum ParseOrderResultState
    {
        Unknown,
        OK,
        OrganisationNotFound,
        OneOrMoreProductsNotFound,
        AtLeastOneProblem
    }


    public class ParseOrderLineResult
    {
        public ParseOrderLineResultState State { get; set; }
        public OrderLine OrderLine { get; set; }
        public string Message { get; set; }

        public ParseOrderLineResult()
        {

        }

        public ParseOrderLineResult(ParseOrderLineResultState state)
        {
            State = state;
        }

        public ParseOrderLineResult(ParseOrderLineResultState state, string message)
        {
            State = state;
            Message = message;
        }

        public ParseOrderLineResult(OrderLine orderLine)
        {
            OrderLine = orderLine;
            State = ParseOrderLineResultState.OK;
        }
    }

    public enum ParseOrderLineResultState
    {
        Unknown,
        OK,
        ParseError,
        OrganisationNotFound,
        ProductNotFound,
        ProductOptionNotFound,
        InvalidProductOptionValue
    }

}
