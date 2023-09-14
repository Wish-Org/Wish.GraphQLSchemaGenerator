using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Wish.GraphQLSchemaGenerator
{
    public delegate Task<JsonDocument> SendGraphQLQueryAsync(string graphqlQuery);

    public class GraphQLTypeGenerator
    {
        //we go quite deep because ofType is used for non-nullable and list
        //example: orderes: [[String!]!]! would require 5 levels deep
        public const string INTROSPECTION_QUERY = """
            fragment fragType on __Type {
              name
              kind
              ofType {
                name
                kind
                ofType {
                  name
                  kind
                  ofType {
                    name
                    kind
                    ofType {
                      name
                      kind
                      ofType {
                        name
                        kind
                      }
                    }
                  }
                }
              }
            }

            fragment fragField on __Field {
              name
              description
              type {
                ...fragType
              }
            }

            {
              __schema {
                types {
                  kind
                  name
                  description
                  fields(includeDeprecated: true) {
                    ...fragField
                  }
                  interfaces {
                    ...fragType
                    fields(includeDeprecated: true) {
                      ...fragField
                    }
                  }
                  possibleTypes {
                    ...fragType
                    fields(includeDeprecated: true) {
                      ...fragField
                    }
                    interfaces {
                      ...fragType
                    }
                  }
                  enumValues(includeDeprecated: true) {
                    name
                    description
                  }
                  ofType {
                    ...fragType
                  }
                }
              }
            }
            """;

        private static readonly Dictionary<string, string> _builtInScalarNameToTypeName = new()
            {
                { "String", "string" },
                { "Int", "int?" },
                { "Float", "float?" },
                { "Boolean", "bool?" },
                { "ID", "string" },
            };

        public async Task<string> GenerateTypesAsync(string @namespace, Dictionary<string, string> scalarNameToTypeName, SendGraphQLQueryAsync sendQuery)
        {
            var response = await sendQuery(INTROSPECTION_QUERY);
            return GenerateTypes(@namespace, scalarNameToTypeName, response);
        }

        public string GenerateTypes(string @namespace, Dictionary<string, string> scalarNameToTypeName, JsonDocument introspectionQueryResponse)
        {
            var types = introspectionQueryResponse.RootElement
                                              .GetProperty("__schema")
                                              .GetProperty("types")
                                              .Deserialize<GraphQLType[]>();

            var str = new StringBuilder()
                            .AppendLine("using System;")
                            .AppendLine("using System.Text.Json.Serialization;")
                            .AppendLine($"namespace {@namespace} {{");

            types.Select(t => GenerateType(t, scalarNameToTypeName))
                 .ForEach(strType => str.Append(strType)
                                        .AppendLine());

            str.AppendLine("}");

            string code = str.ToString();
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = (CSharpSyntaxNode)tree.GetRoot();
            string formattedCode = root.NormalizeWhitespace().ToString();
            return formattedCode;
        }

        private StringBuilder GenerateType(GraphQLType type, Dictionary<string, string> scalarNameToTypeName)
        {
            return type.kind switch
            {
                GraphQLTypeKind.SCALAR or GraphQLTypeKind.INPUT_OBJECT => new StringBuilder(),
                GraphQLTypeKind.ENUM => GenerateEnum(type),
                GraphQLTypeKind.INTERFACE or GraphQLTypeKind.OBJECT => GenerateClassOrInterface(type, scalarNameToTypeName),
                GraphQLTypeKind.UNION => GenerateUnion(type, scalarNameToTypeName),
                _ => throw new Exception($"Unexpected type kind {type.kind}")
            };
        }

        private StringBuilder GenerateUnion(GraphQLType type, Dictionary<string, string> scalarNameToTypeName)
        {
            var str = new StringBuilder()
                            .AppendLine(GenerateDescriptionComment(type.description))
                            .AppendLine("[JsonPolymorphic(TypeDiscriminatorPropertyName = \"__typename\")]");

            foreach (var t in type.possibleTypes)
            {
                str.AppendLine($"[JsonDerivedType(typeof({GenerateTypeName(t, scalarNameToTypeName)}), typeDiscriminator: \"{t.name}\")]");
            }

            str.AppendLine($"public interface {GenerateTypeName(type, scalarNameToTypeName)}");

            str.AppendLine("{");

            var commonFields = type.possibleTypes.First().fields.AsEnumerable();
            foreach (var t in type.possibleTypes.Skip(1))
            {
                commonFields = commonFields.IntersectBy(t.fields.Select(f => (f.type.name, f.name)), f => (f.type.name, f.name));
            }

            commonFields
                .ForEach(f => str.Append(GenerateField(type, f, scalarNameToTypeName)));

            str.AppendLine("}");

            return str;
        }

        private StringBuilder GenerateClassOrInterface(GraphQLType type, Dictionary<string, string> scalarNameToTypeName)
        {
            var str = new StringBuilder()
                            .AppendLine(GenerateDescriptionComment(type.description))
                            .Append($"public {(type.kind is GraphQLTypeKind.INTERFACE or GraphQLTypeKind.UNION ? "interface" : "class")} {GenerateTypeName(type, scalarNameToTypeName)}");

            if (type.interfaces.Any())
                str.Append($" : {string.Join(',', type.interfaces.Select(i => this.GenerateTypeName(i, scalarNameToTypeName)))}");
            str.AppendLine();
            str.AppendLine("{");

            type.fields
                //interface shouldn't redeclare fields already declare in parent interfaces
                .Where(f => type.kind != GraphQLTypeKind.INTERFACE || type.interfaces.SelectMany(i => i.fields).Where(f2 => f2.name == f.name).IsEmpty())
                .ForEach(f => str.Append(GenerateField(type, f, scalarNameToTypeName)));

            str.AppendLine("}");
            return str;
        }

        private StringBuilder GenerateField(GraphQLType containingType, GraphQLField f, Dictionary<string, string> scalarNameToTypeName)
        {
            var str = new StringBuilder()
                            .AppendLine(GenerateDescriptionComment(f.description))
                            .AppendLine($"public {this.GenerateTypeName(f.type, scalarNameToTypeName)}{(f.type.kind == GraphQLTypeKind.ENUM ? "?" : string.Empty)} {EscapeCSharpKeyword(f.name)} {{ {(containingType.kind == GraphQLTypeKind.INTERFACE ? "get;" : "get;set;")} }}")
                            .AppendLine();
            return str;
        }

        private string GenerateTypeName(GraphQLType type, Dictionary<string, string> scalarNameToTypeName)
        {
            if (type.kind == GraphQLTypeKind.NON_NULL)
                return GenerateTypeName(type.ofType, scalarNameToTypeName);

            if (type.kind == GraphQLTypeKind.LIST)
                return GenerateTypeName(type.ofType, scalarNameToTypeName) + "[]";

            return (type.kind is GraphQLTypeKind.INTERFACE or GraphQLTypeKind.UNION ? "I" : string.Empty) +
                                                                (type.kind == GraphQLTypeKind.SCALAR ? this.GetScalarTypeName(type.name, scalarNameToTypeName) : type.name);
        }

        private string GetScalarTypeName(string typeName, Dictionary<string, string> scalarNameToTypeName)
        {
            if (scalarNameToTypeName.ContainsKey(typeName))
                return scalarNameToTypeName[typeName];

            if (_builtInScalarNameToTypeName.ContainsKey(typeName))
                return _builtInScalarNameToTypeName[typeName];

            throw new Exception($"Unknown scalar type '{typeName}'. Please provide a target type for this type.");
        }

        private StringBuilder GenerateEnum(GraphQLType type)
        {
            var str = new StringBuilder().AppendLine(GenerateDescriptionComment(type.description))
                            .AppendLine($"public enum {type.name} {{");

            type.enumValues
                .ForEach(v => str.AppendLine(GenerateDescriptionComment(v.description))
                                 .AppendLine($"{v.name},"));

            str.AppendLine("}");

            return str;
        }

        private string GenerateDescriptionComment(string desc)
        {
            if (desc == null)
                return string.Empty;

            return $"""
                        ///<summary>
                        ///{desc.TrimEnd('\n').Replace("\n", "\n///")}
                        ///</summary>
                    """;
        }

        private string EscapeCSharpKeyword(string fieldName)
        {
            if (SyntaxFactory.ParseTokens(fieldName).First().IsKeyword())
                return "@" + fieldName;
            return fieldName;
        }
    }
}
