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
              isDeprecated
              deprecationReason
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
                    isDeprecated
                    deprecationReason
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
                { "Int", "int" },
                { "Float", "float" },
                { "Boolean", "bool" },
                { "ID", "string" },
            };

        public async Task<string> GenerateTypesAsync(string @namespace, Dictionary<string, string> scalarNameToTypeName, SendGraphQLQueryAsync sendQuery)
        {
            var response = await sendQuery(INTROSPECTION_QUERY);
            return GenerateTypes(@namespace, scalarNameToTypeName, response);
        }

        public string GenerateTypes(string @namespace, Dictionary<string, string> scalarNameToTypeName, JsonDocument introspectionQueryResponse)
        {
            //get the "data.__schema" element or "__schema" element if the "data" property doesn't exist
            var schemaElt = introspectionQueryResponse.RootElement.TryGetProperty("data", out var dataElt) ?
                                                                                dataElt.GetProperty("__schema") :
                                                                                introspectionQueryResponse.RootElement.GetProperty("__schema");
            var allTypes = schemaElt.GetProperty("types")
                                 .Deserialize<GraphQLType[]>();

            var str = new StringBuilder()
                            .AppendLine("using System;")
                            .AppendLine("using System.Text.Json.Serialization;")
                            .AppendLine($"namespace {@namespace} {{");

            var objectTypeNameToUnionTypes = allTypes.Where(t => t.kind == GraphQLTypeKind.UNION)
                                              .SelectMany(tUnion => tUnion.possibleTypes.Select(tObject => (tUnion, tObject)))
                                              .ToLookup(i => i.tObject.name, i => i.tUnion);

            allTypes.Select(t => GenerateType(t, scalarNameToTypeName, objectTypeNameToUnionTypes))
                 .ForEach(strType => str.Append(strType)
                                        .AppendLine());

            str.AppendLine("}");

            string code = str.ToString();
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = (CSharpSyntaxNode)tree.GetRoot();
            string formattedCode = root.NormalizeWhitespace().ToString();
            return "#nullable enable\r\n" + formattedCode;
        }

        private StringBuilder GenerateType(GraphQLType type, Dictionary<string, string> scalarNameToTypeName, ILookup<string, GraphQLType> objectTypeNameToUnionTypes)
        {
            return type.kind switch
            {
                GraphQLTypeKind.SCALAR or GraphQLTypeKind.INPUT_OBJECT => new StringBuilder(),
                GraphQLTypeKind.ENUM => GenerateEnum(type),
                GraphQLTypeKind.OBJECT => GenerateClass(type, scalarNameToTypeName, objectTypeNameToUnionTypes),
                GraphQLTypeKind.INTERFACE => GenerateInterface(type, scalarNameToTypeName),
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

            foreach (var t in type.possibleTypes)
            {
                var typeName = GenerateTypeName(t, scalarNameToTypeName);
                str.AppendLine($"public {typeName}? As{typeName}() => this as {typeName};");
            }

            var commonFields = type.possibleTypes.First().fields.AsEnumerable();
            foreach (var t in type.possibleTypes.Skip(1))
            {
                commonFields = commonFields.IntersectBy(t.fields.Select(f => (GenerateTypeName(f.type, scalarNameToTypeName), f.name)),
                                                        f => (GenerateTypeName(f.type, scalarNameToTypeName), f.name));
            }

            commonFields
                .ForEach(f => str.Append(GenerateField(type, f, scalarNameToTypeName)));

            str.AppendLine("}");

            return str;
        }

        private StringBuilder GenerateInterface(GraphQLType type, Dictionary<string, string> scalarNameToTypeName)
        {
            var str = new StringBuilder()
                            .AppendLine(GenerateDescriptionComment(type.description))
                            .AppendLine("[JsonPolymorphic(TypeDiscriminatorPropertyName = \"__typename\")]");

            foreach (var t in type.possibleTypes)
            {
                str.AppendLine($"[JsonDerivedType(typeof({GenerateTypeName(t, scalarNameToTypeName)}), typeDiscriminator: \"{t.name}\")]");
            }

            str.AppendLine($"public interface {GenerateTypeName(type, scalarNameToTypeName)}");

            var interfaces = type.interfaces;
            if (interfaces.Any())
                str.Append($" : {string.Join(',', interfaces.Select(i => this.GenerateTypeName(i, scalarNameToTypeName)))}");
            str.AppendLine();
            str.AppendLine("{");

            if (type.interfaces.IsEmpty())
            {
                foreach (var t in type.possibleTypes.DistinctBy(i => i.name))//found case where same type included twice
                {
                    var typeName = GenerateTypeName(t, scalarNameToTypeName);
                    str.AppendLine($"public {typeName}? As{typeName}() => this as {typeName};");
                }
            }

            type.fields
                //interface shouldn't redeclare fields already declare in parent interfaces
                .Where(f => type.interfaces.SelectMany(i => i.fields).Where(f2 => f2.name == f.name).IsEmpty())
                .ForEach(f => str.Append(GenerateField(type, f, scalarNameToTypeName)));

            str.AppendLine("}");
            return str;
        }


        private StringBuilder GenerateClass(GraphQLType type, Dictionary<string, string> scalarNameToTypeName, ILookup<string, GraphQLType> objectTypeNameToUnionTypes)
        {
            var str = new StringBuilder()
                            .AppendLine(GenerateDescriptionComment(type.description))
                            .Append($"public class {GenerateTypeName(type, scalarNameToTypeName)}");

            var interfaces = type.interfaces.Concat(objectTypeNameToUnionTypes[type.name]);
            if (interfaces.Any())
                str.Append($" : {string.Join(',', interfaces.Select(i => this.GenerateTypeName(i, scalarNameToTypeName)))}");
            str.AppendLine();
            str.AppendLine("{");

            type.fields
                .ForEach(f => str.Append(GenerateField(type, f, scalarNameToTypeName)));

            str.AppendLine("}");
            return str;
        }

        private StringBuilder GenerateField(GraphQLType containingType, GraphQLField f, Dictionary<string, string> scalarNameToTypeName)
        {
            var str = new StringBuilder()
                            .AppendLine(GenerateDescriptionComment(f.description));
            if (f.isDeprecated)
                str.AppendLine($"[Obsolete({SymbolDisplay.FormatLiteral(f.deprecationReason.TrimEnd(), true)})]");
            str.AppendLine($"public {this.GenerateTypeName(f.type, scalarNameToTypeName)}? {EscapeCSharpKeyword(f.name)} {{ {(containingType.kind == GraphQLTypeKind.INTERFACE ? "get;" : "get;set;")} }}")
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
                .ForEach(v =>
                {
                    str.AppendLine(GenerateDescriptionComment(v.description));
                    if (v.isDeprecated)
                        str.AppendLine($"[Obsolete({SymbolDisplay.FormatLiteral(v.deprecationReason.TrimEnd(), true)})]");
                    str.AppendLine($"{v.name},");
                });

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
