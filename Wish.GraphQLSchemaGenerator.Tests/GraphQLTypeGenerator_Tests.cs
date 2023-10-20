using Microsoft.VisualStudio.TestTools.UnitTesting;
using shopify;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Wish.GraphQLSchemaGenerator.Tests
{
    [TestClass]
    public class GraphQLTypeGenerator_Tests
    {
        [TestMethod]
        public void GenerateShopifyTypes()
        {
            var scalarNameToTypeName = new Dictionary<string, string>
            {
                { "UnsignedInt64", "ulong" },
                { "Money", "decimal" },
                { "Decimal", "decimal" },
                { "DateTime", "DateTime" },
                { "Date", "DateOnly" },
                { "UtcOffset", "TimeSpan" },
                { "URL", "string" },
                { "HTML", "string" },
                { "JSON", "string" },
                { "FormattedString", "string" },
                { "ARN", "string" },
                { "StorefrontID", "string" }
            };

            var generator = new GraphQLTypeGenerator();
            var shopifyDoc = JsonDocument.Parse(File.OpenRead(@"./shopify.json"));
            var code = generator.GenerateTypes("shopify", scalarNameToTypeName, shopifyDoc);
            File.WriteAllText("../../../shopify.cs", code);
        }

        [TestMethod]
        public void SerializerMustRoundTrip()
        {
            var order1 = new Order
            {
                id = "123",
                customer = new Customer
                {
                    id = "234"
                },
                purchasingEntity = new PurchasingCompany
                {
                    contact = new CompanyContact
                    {
                        id = "456"
                    }
                }
            };
            string json1 = order1.ToJson();
            var order2 = Order.FromJson(json1);
            var json2 = order2.ToJson();
            Assert.AreEqual(json1, json2);
        }
    }
}