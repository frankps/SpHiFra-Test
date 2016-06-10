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
using Dvit.OpenBiz.Services.Data;

namespace Dvit.OpenBiz.Services.OrderProcessing
{
    public class OrderWithContext
    {
        InMemoryCache _Cache = null;
        Dictionary<string, Product> _ProductsDictionary = null;

        public Order Order { get; set; }

        public OrderWithContext(Order order, InMemoryCache cache)
        {
            Order = order;
            _Cache = cache;
        }

        public Dictionary<string, Product> ProductsDictionary
        {
            get
            {

                if (_ProductsDictionary == null)
                {   
                    _ProductsDictionary = _Cache.GetProductsAsDictionary(Order.OrderLines.Select(ol => ol.ProductId));
                }

                return _ProductsDictionary;

            }
        }

        public Product GetProduct(string productId)
        {
            if (_ProductsDictionary.ContainsKey(productId))
                return _ProductsDictionary[productId];
            else
                return null;
        }

        public List<Product> GetProducts(Pcl.ProductType type)
        {
            return this.ProductsDictionary.Values.Where(p => p.ProductType == type).ToList();
        }

        public List<string> GetProductIds(Pcl.ProductType type)
        {
            return this.ProductsDictionary.Values.Where(p => p.ProductType == type).Select(p =>p.Id).ToList();
        }

        public int CountProducts(Pcl.ProductType type)
        {
            return this.ProductsDictionary.Values.Count(p => p.ProductType == type);
        }



    }
}
