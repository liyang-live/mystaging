﻿using MyStaging.Common;
using MyStaging.Core;
using MyStaging.Metadata;
using MyStaging.Interface;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MyStaging.PostgreSQL.Generals
{
    public class GeneralFactory : IGeneralFactory
    {
        private DbContext dbContext;

        public void Initialize(ProjectConfig config)
        {
            Tables = new List<TableInfo>();
            StagingOptions options = new StagingOptions(config.ProjectName, config.ConnectionString)
            {
                Provider = ProviderType.PostgreSQL
            };
            dbContext = new PgDbContext(options);

            #region dir

            CheckNotNull.NotEmpty(config.ProjectName, nameof(config.ProjectName));

            if (config.Mode == GeneralMode.Db)
            {
                CheckNotNull.NotEmpty(config.OutputDir, nameof(config.OutputDir));
                Config = new GeneralConfig
                {
                    OutputDir = config.OutputDir,
                    ProjectName = config.ProjectName,
                    ModelPath = Path.Combine(config.OutputDir, "Models")
                };

                if (!Directory.Exists(Config.ModelPath))
                    Directory.CreateDirectory(Config.ModelPath);
            }
            #endregion

            #region Schemas
            string[] filters = new string[this.Filters.Count];
            for (int i = 0; i < Filters.Count; i++)
            {
                filters[i] = $"'{Filters[i]}'";
            }

            string sql = $@"SELECT schema_name FROM information_schema.schemata WHERE SCHEMA_NAME NOT IN({string.Join(",", filters)}) ORDER BY SCHEMA_NAME; ";
            List<string> schemas = new List<string>();
            dbContext.Execute.ExecuteDataReader(dr =>
            {
                schemas.Add(dr[0].ToString());
            }, CommandType.Text, sql);
            #endregion

            #region Tables
            foreach (var schema in schemas)
            {
                string _sqltext = $@"SELECT table_name,'table' as type FROM INFORMATION_SCHEMA.tables WHERE table_schema='{schema}' AND table_type='BASE TABLE'
UNION ALL
SELECT table_name,'view' as type FROM INFORMATION_SCHEMA.views WHERE table_schema = '{schema}'";
                dbContext.Execute.ExecuteDataReader(dr =>
                {
                    var table = new TableInfo()
                    {
                        Schema = schema,
                        Name = dr["table_name"].ToString(),
                        Type = dr["type"].ToString() == "table" ? TableType.Table : TableType.View
                    };
                    GetFields(table);
                    Tables.Add(table);
                }, CommandType.Text, _sqltext);

            }
            #endregion
        }

        public void DbFirst(ProjectConfig config)
        {
            Initialize(config);
            GenerateMapping();

            // Generral Entity
            foreach (var table in Tables)
            {
                Console.WriteLine("[{0}]{1}.{2}", table.Type, table.Schema, table.Name);
                EntityGeneral td = new EntityGeneral(Config, table);
                td.Create();
            }
        }

        public void CodeFirst(ProjectConfig config)
        {
            Initialize(config);

            StringBuilder sb = new StringBuilder();
            List<TableInfo> tables = new List<TableInfo>();

            var fileName = config.ProjectName + ".dll";
            var dir = System.IO.Directory.GetCurrentDirectory();

            var providerFile = System.IO.Directory.GetFiles(dir, fileName, SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrEmpty(providerFile))
                throw new FileNotFoundException($"在 {dir} 搜索不到文件 {fileName}");

            var types = Assembly.LoadFrom(providerFile).GetTypes();
            foreach (var t in types)
            {
                var tableAttribute = t.GetCustomAttribute<TableAttribute>();
                if (tableAttribute == null)
                    continue;

                var newTable = new TableInfo
                {
                    Name = tableAttribute.Name,
                    Schema = tableAttribute.Schema
                };

                SerializeField(newTable, t);

                var oldTable = Tables.Where(f => f.Schema == newTable.Schema && f.Name == newTable.Name).FirstOrDefault();
                if (oldTable == null) // CREATE
                    DumpTable(newTable, ref sb);
                else // ALTER
                    DumpAlter(newTable, oldTable, ref sb);
            }

            var sql = sb.ToString();

            if (string.IsNullOrEmpty(sql))
            {
                Console.WriteLine("数据模型没有可执行的更改.");
            }
            else
            {
                Console.WriteLine("------------------SQL------------------");
                Console.WriteLine(sql);
                Console.WriteLine("------------------SQL END------------------");
                dbContext.Execute.ExecuteNonQuery(CommandType.Text, sql);
            }
        }

        private void DumpAlter(TableInfo newTable, TableInfo oldTable, ref StringBuilder sb)
        {
            var alterSql = $"ALTER TABLE {newTable.Schema}.{newTable.Name}";

            // 常规
            foreach (var newFi in newTable.Fields)
            {
                var oldFi = oldTable.Fields.Where(f => f.Name == newFi.Name).FirstOrDefault();
                var notNull = newFi.NotNull ? "NOT NULL" : "NULL";
                if (oldFi == null)
                {
                    sb.AppendLine(alterSql + $" ADD {newFi.Name} {newFi.DbType}{GetLengthString(newFi)};");
                    sb.AppendLine(alterSql + $" MODIFY {newFi.Name} {newFi.DbType} {notNull};");
                }
                else
                {
                    if (oldFi.DbType != newFi.DbType || (oldFi.Length != newFi.Length && GetLengthString(newFi) != null))
                        sb.AppendLine(alterSql + $" ALTER {newFi.Name} TYPE {newFi.DbType}{GetLengthString(newFi)};");
                    if (oldFi.NotNull != newFi.NotNull)
                    {
                        sb.AppendLine(alterSql + $" MODIFY {newFi.Name} {newFi.DbType} {notNull};");
                    }
                }
            }

            // 检查旧约束
            List<string> primaryKeys = new List<string>();
            foreach (var c in oldTable.Constraints)
            {
                var constraint = newTable.Fields.Where(f => f.Name == c.Field).FirstOrDefault();
                if (constraint == null)
                {
                    sb.AppendLine(alterSql + $" DROP CONSTRAINT {c.Name};");
                }
            }

            // 检查新约束
            var pks = newTable.Fields.Where(f => f.Identity);
            foreach (var p in pks)
            {
                var constraint = oldTable.Constraints.Where(f => f.Field == p.Name).FirstOrDefault();
                if (constraint == null)
                {
                    sb.AppendLine(alterSql + $" ADD CONSTRAINT pk_{newTable.Name} PRIMARY KEY({p.Name});");
                }
            }
        }

        private void SerializeField(TableInfo table, Type type)
        {
            var properties = MyStagingUtils.GetDbFields(type);
            foreach (var pi in properties)
            {
                var fi = new DbFieldInfo();
                fi.Name = pi.Name;
                var customAttributes = pi.GetCustomAttributes();
                var genericAttrs = customAttributes.Select(f => f.GetType()).ToArray();
                if (pi.PropertyType.Name == "Nullable`1")
                {
                    fi.NotNull = false;
                    fi.CsType = pi.PropertyType.GenericTypeArguments[0].Name;
                }
                else
                {
                    fi.CsType = pi.PropertyType.Name;
                    if (pi.PropertyType == typeof(string))
                    {
                        fi.NotNull = genericAttrs.Where(f => f == typeof(RequiredAttribute) || f == typeof(KeyAttribute)).FirstOrDefault() != null;
                    }
                    else
                    {
                        fi.NotNull = pi.PropertyType.IsValueType;
                    }
                }
                fi.Identity = genericAttrs.Where(f => f == typeof(KeyAttribute)).FirstOrDefault() != null;
                var lengthAttribute = customAttributes.Where(f => f.GetType() == typeof(StringLengthAttribute)).FirstOrDefault();
                if (lengthAttribute != null)
                {
                    var lenAttribute = ((StringLengthAttribute)lengthAttribute);
                    fi.Length = lenAttribute.MaximumLength;
                    fi.Numeric_scale = lenAttribute.MinimumLength;
                }
                var dtAttribute = pi.GetCustomAttribute<DataTypeAttribute>();
                if (dtAttribute != null)
                {
                    if (string.IsNullOrEmpty(dtAttribute.CustomDataType)) throw new KeyNotFoundException($"找不到属性{table.Name}.{pi.Name}的对应数据库类型，请为该属性设置DataTypeAttribute，并指定 CustomDataType 的值");

                    fi.DbType = dtAttribute.CustomDataType;
                }
                else
                {
                    fi.DbType = PgsqlType.GetDbType(fi.CsType.Replace("[]", ""));
                }
                fi.IsArray = fi.CsType.Contains("[]");

                table.Fields.Add(fi);
            }
        }

        private void DumpTable(TableInfo table, ref StringBuilder sb)
        {
            sb.AppendLine($"CREATE TABLE {table.Schema}.{table.Name}");
            sb.AppendLine("(");
            int length = table.Fields.Count;
            for (int i = 0; i < length; i++)
            {
                var fi = table.Fields[i];

                sb.AppendFormat("  \"{0}\" {1}{2}{3} {4} {5}{6}",
                    fi.Name,
                    fi.DbType,
                    GetLengthString(fi),
                    fi.IsArray ? "[]" : "",
                    fi.Identity ? "PRIMARY KEY" : "",
                    fi.Identity || fi.NotNull ? "NOT NULL" : "NULL",
                    (i + 1 == length) ? "" : ","
                    );
                sb.AppendLine();
            }
            sb.AppendLine(")");
            sb.AppendLine("WITH (OIDS=FALSE);");
        }

        private string GetLengthString(DbFieldInfo fi)
        {
            string lengthString = null;
            if (fi.Length > 0)
            {
                if (fi.Length != 255 && fi.CsType == "String")
                    lengthString = $"({fi.Length})";
                else if (fi.CsType != "String" && fi.Numeric_scale > 0)
                {
                    lengthString = $"({fi.Length},{fi.Numeric_scale})";
                }
            }

            return lengthString;
        }

        public void GenerateMapping()
        {
            string _sqltext = @"
select a.oid,a.typname,b.nspname from pg_type a 
INNER JOIN pg_namespace b on a.typnamespace = b.oid 
where a.typtype = 'e' order by oid asc";

            List<EnumTypeInfo> enums = new List<EnumTypeInfo>();
            dbContext.Execute.ExecuteDataReader(dr =>
            {
                enums.Add(new EnumTypeInfo()
                {
                    Oid = Convert.ToInt32(dr["oid"]),
                    TypeName = dr["typname"].ToString(),
                    NspName = dr["nspname"].ToString()
                });
            }, System.Data.CommandType.Text, _sqltext);

            if (enums.Count > 0)
            {
                string _fileName = Path.Combine(Config.ModelPath, "_Enums.cs");
                using StreamWriter writer = new StreamWriter(File.Create(_fileName), System.Text.Encoding.UTF8);
                writer.WriteLine("using System;");
                writer.WriteLine();
                writer.WriteLine($"namespace {Config.ProjectName}.Model");
                writer.WriteLine("{");

                for (int i = 0; i < enums.Count; i++)
                {
                    var item = enums[i];
                    writer.WriteLine($"\tpublic enum {item.TypeName}");
                    writer.WriteLine("\t{");
                    string sql = $"select oid,enumlabel from pg_enum WHERE enumtypid = {item.Oid} ORDER BY oid asc";
                    dbContext.Execute.ExecuteDataReader(dr =>
                    {
                        string c = i < enums.Count ? "," : "";
                        writer.WriteLine($"\t\t{dr["enumlabel"]}{c}");
                    }, CommandType.Text, sql);
                    writer.WriteLine("\t}");
                }
                writer.WriteLine("}");
            }

            var contextName = $"{ Config.ProjectName }DbContext";
            string _startup_file = Path.Combine(Config.OutputDir, $"{contextName}.cs");
            using (StreamWriter writer = new StreamWriter(File.Create(_startup_file), System.Text.Encoding.UTF8))
            {
                writer.WriteLine($"using {Config.ProjectName}.Model;");
                writer.WriteLine("using System;");
                writer.WriteLine("using Npgsql;");
                writer.WriteLine("using MyStaging.Core;");
                writer.WriteLine("using MyStaging.Common;");
                writer.WriteLine("using Newtonsoft.Json.Linq;");
                writer.WriteLine();
                writer.WriteLine($"namespace {Config.ProjectName}");
                writer.WriteLine("{");
                writer.WriteLine($"\tpublic class {contextName} : DbContext");
                writer.WriteLine("\t{");
                writer.WriteLine($"\t\tpublic {contextName}(StagingOptions options) : base(options, ProviderType.PostgreSQL)");
                writer.WriteLine("\t\t{");
                writer.WriteLine("\t\t}");
                writer.WriteLine();
                writer.WriteLine($"\t\tstatic {contextName}()");
                writer.WriteLine("\t\t{");
                writer.WriteLine("\t\t\tType[] jsonTypes = { typeof(JToken), typeof(JObject), typeof(JArray) };");
                writer.WriteLine("\t\t\tNpgsqlNameTranslator translator = new NpgsqlNameTranslator();");
                writer.WriteLine("\t\t\tNpgsqlConnection.GlobalTypeMapper.UseJsonNet(jsonTypes);");

                foreach (var table in Tables)
                {
                    if (table.Name == "geometry_columns")
                    {
                        writer.WriteLine($"\t\t\tNpgsqlConnection.GlobalTypeMapper.UseLegacyPostgis();");
                        break;
                    }
                }

                if (enums.Count > 0)
                {
                    writer.WriteLine();
                    foreach (var item in enums)
                    {
                        writer.WriteLine($"\t\t\tNpgsqlConnection.GlobalTypeMapper.MapEnum<{item.TypeName}>(\"{item.NspName}.{item.TypeName}\", translator);");
                    }
                }

                writer.WriteLine("\t\t}"); // InitializerMapping end
                writer.WriteLine();

                foreach (var table in Tables)
                {
                    writer.WriteLine($"\t\tpublic DbSet<{table.Name.ToUpperPascal()}Model> {table.Name.ToUpperPascal()} {{ get; set; }}");
                }

                writer.WriteLine("\t}"); // class end
                writer.WriteLine("\tpublic partial class NpgsqlNameTranslator : INpgsqlNameTranslator");
                writer.WriteLine("\t{");
                writer.WriteLine("\t\tpublic string TranslateMemberName(string clrName) => clrName;");
                writer.WriteLine("\t\tpublic string TranslateTypeName(string clrTypeName) => clrTypeName;");
                writer.WriteLine("\t}");
                writer.WriteLine("}"); // namespace end
            }
        }

        private void GetFields(TableInfo table)
        {
            string _sqltext = @"SELECT a.oid
                                            ,c.attnum as num
                                            ,c.attname as field
                                            ,c.attnotnull as notnull
                                            ,d.description as comment
                                            ,(case when e.typcategory ='G' then e.typname when e.typelem = 0 then e.typname else e2.typname end) as type
                                            ,(case when e.typelem = 0 then e.typtype else e2.typtype end) as data_type
                                            ,COALESCE((
                                            case 
                                            when (case when e.typcategory ='G' then e.typname when e.typelem = 0 then e.typname else e2.typname end) in ('numeric','int2','int4','int8','float4','float8') then f.numeric_precision
                                            when (case when e.typcategory ='G' then e.typname when e.typelem = 0 then e.typname else e2.typname end) in ('timestamp','timestamptz','interval','time','date','timetz') then f.datetime_precision
                                            when f.character_maximum_length is null then 0
                                            else f.character_maximum_length 
                                            end
                                            ),0) as length
                                            ,COALESCE((
                                            case 
                                            when (case when e.typcategory ='G' then e.typname when e.typelem = 0 then e.typname else e2.typname end) in ('numeric') then f.numeric_scale
                                            else 0
                                            end
                                            ),0) numeric_scale
                                            ,e.typcategory
                                            ,f.udt_schema
                                                                            from  pg_class a 
                                                                            inner join pg_namespace b on a.relnamespace=b.oid
                                                                            inner join pg_attribute c on attrelid = a.oid
                                                                            LEFT OUTER JOIN pg_description d ON c.attrelid = d.objoid AND c.attnum = d.objsubid and c.attnum > 0
                                                                            inner join pg_type e on e.oid=c.atttypid
                                                                            left join pg_type e2 on e2.oid=e.typelem
                                                                            inner join information_schema.columns f on f.table_schema = b.nspname and f.table_name=a.relname and column_name = c.attname
                                                                            WHERE b.nspname='{0}' and a.relname='{1}';";

            _sqltext = string.Format(_sqltext, table.Schema, table.Name);
            dbContext.Execute.ExecuteDataReader(dr =>
            {
                DbFieldInfo fi = new DbFieldInfo
                {
                    Oid = Convert.ToInt32(dr["oid"]),
                    Name = dr["field"].ToString(),
                    Length = Convert.ToInt32(dr["length"].ToString()),
                    NotNull = Convert.ToBoolean(dr["notnull"]),
                    Comment = dr["comment"].ToString(),
                    Numeric_scale = Convert.ToInt32(dr["numeric_scale"].ToString()),
                };

                var udt_schema = dr["udt_schema"].ToString();
                var typcategory = dr["typcategory"].ToString();
                var dbtype = dr["type"].ToString();
                fi.DbType = typcategory == "E" ? udt_schema + "." + dbtype : dbtype;
                fi.IsArray = typcategory == "A";
                fi.CsType = PgsqlType.SwitchToCSharp(dbtype);

                string _notnull = "";
                if (
                fi.CsType != "string"
                && fi.CsType != "byte[]"
                && fi.CsType != "JToken"
                && !fi.IsArray
                && fi.CsType != "System.Net.IPAddress"
                && fi.CsType != "System.Net.NetworkInformation.PhysicalAddress"
                && fi.CsType != "System.Xml.Linq.XDocument"
                && fi.CsType != "System.Collections.BitArray"
                && fi.CsType != "object"
                )
                    _notnull = fi.NotNull ? "" : "?";

                string _array = fi.IsArray ? "[]" : "";
                fi.RelType = $"{fi.CsType}{_notnull}{_array}";

                table.Fields.Add(fi);
            }, CommandType.Text, _sqltext);

            if (table.Type == TableType.Table)
                GetPrimarykey(table);
        }

        private void GetPrimarykey(TableInfo table)
        {
            string _sqltext = $@"select a.constraint_name,b.column_name 
                                              from information_schema.table_constraints a
                                              inner join information_schema.constraint_column_usage b on a.constraint_name=b.constraint_name
                                              where a.table_schema || '.' || a.table_name='{table.Schema}.{table.Name}' and a.constraint_type='PRIMARY KEY'";

            dbContext.Execute.ExecuteDataReader(dr =>
            {
                var constaint = new ConstraintInfo
                {
                    Field = dr["column_name"].ToString(),
                    Name = dr["constraint_name"].ToString(),
                    Type = ConstraintType.PK
                };

                table.Constraints.Add(constaint);
                table.Fields.Where(f => f.Name == constaint.Field).First().Identity = true;

            }, CommandType.Text, _sqltext);
        }

        #region Properties
        public List<string> Filters { get; set; } = new List<string>() {
               "geometry_columns",
               "raster_columns",
               "spatial_ref_sys",
               "raster_overviews",
               "us_gaz",
               "topology",
               "zip_lookup_all",
               "pg_toast",
               "pg_temp_1",
               "pg_toast_temp_1",
               "pg_catalog",
               "information_schema",
               "tiger",
               "tiger_data"
        };
        public GeneralConfig Config { get; set; }
        public List<TableInfo> Tables { get; set; }
        #endregion
    }
}
