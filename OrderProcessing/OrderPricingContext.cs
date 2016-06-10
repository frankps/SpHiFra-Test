using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Model = Dvit.OpenBiz.Model;
using Dvit.OpenBiz.Model;
using Dvit.OpenBiz.Database;
using NLogLight;
using Dvit.OpenBiz.Services.Data;

namespace Dvit.OpenBiz.Services.OrderProcessing
{
    /*
        Formula interpreters:
     
        https://ncalc.codeplex.com/
        http://flee.codeplex.com/
      
     */
    public class OrderPricingContext
    {
        static Logger _Logger = LogManager.GetLogger("OrderPricingContext");

        OpenBizContext _Ctx = null;
        Order _Order = null;

        List<Product> Products = null;
        List<Catalog> Catalogs = null;
        List<SysObject> TaxClasses = null;
        InMemoryCache cache = new InMemoryCache();

        public OrderPricingContext(Order order, OpenBizContext ctx = null)
        {
            if (ctx == null)
                ctx = new OpenBizContext();

            _Ctx = ctx;

            Init(order);
        }

        public void Init(Order order)
        {
            try
            {
                if (order == null || order.OrderLines == null)
                    return;

                var productIds = order.OrderLines.Select(ol => ol.ProductId);
                Products = cache.GetProducts(productIds);  //_Ctx.Product.Where(p => productIds.Contains(p.Id)).ToList();

                var catalogIds = Products.Select(p => p.CatalogId).Distinct();
                Catalogs = cache.GetCatalogs(catalogIds);   //_Ctx.Catalog.Where(c => catalogIds.Contains(c.Id)).ToList();

                List<string> productOptionValueIds = new List<string>();

                foreach (var orderLine in order.OrderLines)
                {
                    if (orderLine.OrderLineOptions == null) continue;

                    productOptionValueIds.AddRange(orderLine.OrderLineOptions.Where(olo => !string.IsNullOrWhiteSpace(olo.ProductOptionValueId)).Select(olo => olo.ProductOptionValueId));
                }

                // pre-load productOptionValue's (needed to calculate correct price)
                cache.GetProductOptionValues(productOptionValueIds);

                List<string> taxClassIds = new List<string>();

                var productTaxClassIds = Products.Where(p => p.SalesTaxClassId != null).Select(p => p.SalesTaxClassId).Distinct();
                if (productTaxClassIds != null && productTaxClassIds.Count() > 0) taxClassIds.AddRange(productTaxClassIds);

                var catalogTaxClassIds = Catalogs.Where(c => c.DefaultSalesTaxClassId != null).Select(c => c.DefaultSalesTaxClassId).Distinct();
                if (catalogTaxClassIds != null && catalogTaxClassIds.Count() > 0) taxClassIds.AddRange(catalogTaxClassIds);

                TaxClasses = cache.GetSysObjects(taxClassIds);  //_Ctx.SysObject.Where(o => taxClassIds.Contains(o.Id)).ToList();
            }
            catch (Exception ex)
            {
                _Logger.Error(ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// We expect all products in order to come from Catalogs whith the same tax setting (all included or all excluded)
        /// </summary>
        /// <returns></returns>
        public bool IsTaxInclusiveOrder()
        {
            if (this.Catalogs == null || this.Catalogs.Count == 0)
                return true;

            var catalog = Catalogs.FirstOrDefault();

            return catalog.SalesPricesInclTax;
        }

        public void SetPrice(OrderLine orderLine)
        {
            try
            {
                if (orderLine == null)
                    return;

                Product prod = Products.FirstOrDefault(p => p.Id == orderLine.ProductId);
                Catalog catalog = Catalogs.FirstOrDefault(c => c.Id == prod.CatalogId);

                if (prod == null || catalog == null)
                    return;

                if (string.IsNullOrWhiteSpace(orderLine.Description))
                    orderLine.Description = prod.Name;

                // if the orderline has a custom price, then we will NOT re-evaluate it
                if (!orderLine.IsCustomPrice)
                {
                    decimal price = 0;

                    // price component 1: sales price defined on product
                    price = prod.SalesPrice;

                    // price component 2: prices of all the selected option values
                    if (orderLine.OrderLineOptions != null && orderLine.OrderLineOptions.Count > 0)
                    {
                        foreach (OrderLineOption olo in orderLine.OrderLineOptions.Where(olo => !string.IsNullOrWhiteSpace(olo.ProductOptionValueId)))
                        {
                            ProductOptionValue productOptionValue = cache.GetProductOptionValue(olo.ProductOptionValueId);

                            if (productOptionValue != null)
                                price += productOptionValue.Price;
                        }
                    }

                    // price component 3: if there is a formula defined, add the result to the price
                    if (!string.IsNullOrWhiteSpace(prod.SalesPriceFormulaInternal))                   
                    {
                        CalculationSvc calcSvc = new CalculationSvc();
                        decimal formulaPrice = calcSvc.Calculate(prod.SalesPriceFormulaInternal, orderLine);
                        price += formulaPrice;
                    }

                    orderLine.UnitPrice = price;
                }

                orderLine.TotalPrice = Math.Round(orderLine.UnitPrice * orderLine.Quantity,2);

                string taxClassId = null;

                if (prod.SalesTaxClassId != null) taxClassId = prod.SalesTaxClassId;
                else taxClassId = catalog.DefaultSalesTaxClassId;

                orderLine.TaxPctg = 0;

                if (taxClassId != null)
                {
                    SysObject taxClass = TaxClasses.FirstOrDefault(so => so.Id == taxClassId);

                    if (taxClass != null && taxClass.Value.HasValue)
                    {
                        orderLine.TaxPctg = taxClass.Value.Value;
                    }
                }

                orderLine.Tax = Math.Round(orderLine.CalculateTotalTax(),2);
            }
            catch (Exception ex)
            {
                _Logger.Error(ex.ToString());
                throw;
            }
        }
    }
}
