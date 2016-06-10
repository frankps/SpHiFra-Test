using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data.Entity;
using System.Data.Entity.Validation;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Net.Http.Headers;
using Model = Dvit.OpenBiz.Model;
using Dvit.OpenBiz.Model;
using Dvit.OpenBiz.Database;
using Dvit.OpenBiz.Pcl;
using Dvit.OpenBiz.Reservation;
using NLogLight;
using Dvit.OpenBiz.Services.Reservation;
using Dvit.OpenBiz.Services.Data;
using System.Collections.Concurrent;
using EdgeJs;
using System.Dynamic;
using Newtonsoft.Json;

namespace Dvit.OpenBiz.Services.OrderProcessing
{
    public class CalculationSvc
    {
        //public string _121_887 { get; set; }
        private string GuidToVar(string guid)
        {
            guid = guid.Replace("-", "_");
            
            return "_" + guid;
        }
        /*
         * var regExpString = '\\b' + word + '\\b(?!\')';
        formula = formula.replace(new RegExp(regExpString, 'g'), 'values.' + word);
         */
        public string PrepareFormula(string formula, string orgId, string productId, OpenBizContext ctx)
        {

            formula = formula.Replace("\r\n", " ");

            SysLinkTypeSvc _linkTypeSvc = new SysLinkTypeSvc();
            string productOptionLinkTypeId = _linkTypeSvc.ProductOptionLinkTypeId;

            SysLinkSvc<Product, ProductOption> productOptionLinks = new SysLinkSvc<Product, ProductOption>(orgId, productOptionLinkTypeId, productId);

            List<ProductOption> productOptions = productOptionLinks.Type2Objects;
            
            foreach (var prodOption in productOptions)
            {
                var regExp = new Regex("\\b" + prodOption.Code + "\\b");

                formula = regExp.Replace(formula, GuidToVar(prodOption.Id));
                // formula = formula.Replace(prodOption.Code, GuidToVar(prodOption.Id));
            }

            IEnumerable<string> prodOptionIdsWithValues = productOptions.Where(po => po.HasValueObjects).Select(po => po.Id);

            if (prodOptionIdsWithValues != null && prodOptionIdsWithValues.Count() > 0)
            {
                IEnumerable<ProductOptionValue> values = ctx.ProductOptionValue.Where(pov => prodOptionIdsWithValues.Contains(pov.ProductOptionId));

                if (values != null)
                {
                    foreach (ProductOptionValue value in values)
                    {
                        var regExp = new Regex("\\b" + value.Code + "\\b");

                        formula = regExp.Replace(formula, GuidToVar(value.Id));

                    }
                }


                //foreach (var )

            }
            return formula;
        }

        public string PrepareFormula(string formula, IEnumerable<ProductOption> options, IEnumerable<ProductOptionValue> values)
        {
            if (string.IsNullOrWhiteSpace(formula))
                return null;

            if (options != null)
            {
                foreach (var option in options.Where(o => !string.IsNullOrWhiteSpace(o.Code)))
                    formula = formula.Replace(option.Code, GuidToVar(option.Id));
            }

            if (values != null)
            {
                foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v.Code)))
                    formula = formula.Replace(value.Code, GuidToVar(value.Id));
            }

            return formula;
        }


        public class FormulaRequest
        {
            [JsonProperty(PropertyName = "formula")]
            public string Formula { get; set; }

            [JsonProperty(PropertyName = "inputs")]
            public ExpandoObject Inputs { get; set; }
        }

        public class FormulaResult
        {
            [JsonProperty(PropertyName = "result")]
            public decimal Result { get; set; }
        }

        public decimal Calculate(string formula, OrderLine orderLine)
        {
            try
            {
                dynamic inputs = new ExpandoObject();

                IDictionary<string, object> dictionary = (IDictionary<string, object>)inputs;

                dictionary.Add("OrderLineQty", orderLine.Quantity);

                IEnumerable<OrderLineOption> olOptions = orderLine.OrderLineOptions;

                if (olOptions != null)
                {
                    

                    foreach (OrderLineOption olOption in olOptions)
                    {
                        string varName = GuidToVar(olOption.ProductOptionId);

                        if (dictionary.ContainsKey(varName))
                            continue; // duplicate product option values: should not happen (but it did)!!

                        if (!string.IsNullOrWhiteSpace(olOption.ProductOptionValueId))
                        {
                            string varValue = GuidToVar(olOption.ProductOptionValueId);
                            dictionary.Add(varName, varValue);
                        }
                        else
                        {
                            dictionary.Add(varName, olOption.ProductOptionValue);
                        }
                    }
                }

                FormulaRequest req = new FormulaRequest()
                {
                    Formula = formula,
                    Inputs = inputs
                };


                using (var client = new System.Net.Http.HttpClient())
                {
                    // client.BaseAddress = new Uri("http://localhost:5051/");

                    string webApiUrl = Properties.Settings.Default.WebApiUrl;

                    client.BaseAddress = new Uri(webApiUrl);  // "http://webapi-openbiz-dev.azurewebsites.net/"

                    client.DefaultRequestHeaders.Accept.Clear();
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    var response = client.PostAsJsonAsync("nodejs/execFormula.js", req).Result;

                    if (response.IsSuccessStatusCode)
                    {
                        var headers = response.Headers;

                        FormulaResult returnValue = response.Content.ReadAsAsync<FormulaResult>().Result;

                        return returnValue.Result;
                    }
                    else
                    {
                        return 0;
                    }
                }


            }
            catch (Exception ex)
            {
                throw;
            }
        }


        public async Task<decimal> CalculateOld(string formula, IEnumerable<OrderLineOption> olOptions)
        {
            try
            {
                dynamic inputs = new ExpandoObject();

                IDictionary<string, object> dictionary = (IDictionary<string, object>)inputs;

                if (olOptions != null)
                {
                    foreach (OrderLineOption olOption in olOptions)
                    {
                        string varName = GuidToVar(olOption.ProductOptionId);

                        if (!string.IsNullOrWhiteSpace(olOption.ProductOptionValueId))
                        {
                            string varValue = GuidToVar(olOption.ProductOptionValueId);
                            dictionary.Add(varName, varValue);
                        }
                        else
                        {
                            dictionary.Add(varName, olOption.ProductOptionValue);
                        }
                    }
                }

                string jsCode = GetJavaScriptCode(formula);

                var func = Edge.Func(jsCode);

                var res = await func(inputs);


                decimal result = 0;

                decimal.TryParse(res.ToString(), out result);

                return result;
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        public string GetJavaScriptCode(string formula)
        {
            //formula = formula.Replace()

            StringBuilder sbCode = new StringBuilder();

            sbCode.Append(@"
            return function (data, callback) {
");

            sbCode.AppendLine(GetJsFormulaCalculatorCode());

            sbCode.AppendFormat(@"var calculator = new FormulaCalculator(""{0}"");", formula);

            sbCode.Append(@"

var res = calculator.calculate(data);

callback(null, res);
            }
");


            /*
            sbCode.AppendLine(GetJsFormulaCalculatorCode());

            sbCode.AppendFormat("var calculator = new FormulaCalculator('{0}');", formula);

            sbCode.Append(@"

                var res = calculator.calculate(data);

                callback(null, data.duration);
            }
");

             * */

            /*
            var code = string.Format(@"
            return function (data, callback) \{

{0}

var calculator = new FormulaCalculator('{1}');

var res = calculator.calculate(data);

                callback(null, data.duration);
            \}
        ", GetJsFormulaCalculatorCode(), formula);
            */

            return sbCode.ToString();
        }


        public string GetJsFormulaCalculatorCode()
        {

            var code = @"

function FormulaCalculator(formula) {

    this.formula = formula;

    formula = FormulaCalculator.prepareFormula(formula);

    /*
    Calculate price is a dynamic generated method based on the formula defined for the product.
    
    It accepts an object with as properties all the variables used in the formula.
    
    Return value: the price
    */
    this.calculate = FormulaCalculator.createFunction(formula);

}

FormulaCalculator.prepareFormula = function (formula) {

    // search all words in the formula, except words between ''
    var wordsInFormula = formula.match(/[a-zA-Z_][\.\w]*\b(?!')/g);

    // remove the duplicates
    wordsInFormula = wordsInFormula.filter(function (elem, pos) {
        return wordsInFormula.indexOf(elem) == pos;
    });

    // remove reserved words
    wordsInFormula = FormulaCalculator.filterOutReservedWords(wordsInFormula);
    wordsInFormula = FormulaCalculator.filterOutLocalVariables(wordsInFormula);

    // prefix all variables with 'values.' (because these values will come from another object called 'values')
    //look only for perfect match wors and leave out 'word'
    wordsInFormula.forEach(function (word) {
        var regExpString = '\\b' + word + '\\b(?!\')';
        formula = formula.replace(new RegExp(regExpString, 'g'), 'values.' + word);

    });

    //replace all local.xxx by xxx
    formula = formula.replace(new RegExp('local.', 'g'), '');
    //replace min and max with their javascript equivalent
    formula = formula.replace(new RegExp('max', 'g'), 'Math.max');
    formula = formula.replace(new RegExp('min', 'g'), 'Math.min');
    return formula;

}

FormulaCalculator.createFunction = function (formula) {

    /* we will now dynamically create a method:
        input: an object with as properties all the variables used in the formula
        return value: the price
    */

    var stringFunction = '(function calculate(values) { \
 var result = 0; \
' + formula + ' \
 return result; \
})';

    // we sort-of compile this function (because it's still a string)

    var jsFunction = null;

    try {
        jsFunction = eval(stringFunction);
    } catch (err) {
        console.error('There was a problem evaluating the formula:');
        console.error(formula);
        console.error(err);
    }

    // we return the working version => can be used now!!
    return jsFunction;
}

FormulaCalculator.filterOutReservedWords = function (words) {

    var result = [];
    var reservedWords = ['if', 'else', 'result', 'max', 'min'];

    return words.filter(function (word) {
        return reservedWords.indexOf(word) == -1;
    });
}
FormulaCalculator.filterOutLocalVariables = function (words) {

    var result = [];

    return words.filter(function (word) {
        return word.indexOf('local.') != 0;
    });
}

     ";

            return code;

        }



    }
}
