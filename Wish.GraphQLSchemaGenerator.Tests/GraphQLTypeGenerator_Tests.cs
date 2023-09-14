using Microsoft.VisualStudio.TestTools.UnitTesting;
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
            var code = generator.GenerateTypes("Test", scalarNameToTypeName, shopifyDoc);
            File.WriteAllText("../../../shopify.cs", code);
        }
    }
}