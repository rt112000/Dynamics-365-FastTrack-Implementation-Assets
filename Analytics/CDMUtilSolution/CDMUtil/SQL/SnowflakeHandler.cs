// <copyright company="JayConsulting">
// Copyright (c) 2022 All Rights Reserved
// <author>Jay Fu</author>
// https://github.com/snowflakedb/snowflake-connector-net
// </copyright>

using CDMUtil.Context.ObjectDefinitions;
using System.Collections.Generic;
using System;
using System.Data;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using Snowflake.Data.Client;

namespace CDMUtil.Snowflake
{
    public class SnowflakeHandler
    {
        private AppConfigurations c;
        private string SnowflakeConnectionStr;
        private ILogger logger;
        private IDbConnection conn;
        private string dbSchema;
        private string snowflakeExternalStageName;
        private string snowflakeFileFormatName;
        private string existingSnowflakeStorageIntegrationNameWithSchema;
        private string azureDataLakeFileFormatName;
        private string azureDatalakeRootFolder;

        public SnowflakeHandler(AppConfigurations c, ILogger logger)
        {
            this.c = c;
            this.SnowflakeConnectionStr = c.targetSnowflakeDbConnectionString;
            this.dbSchema = c.targetSnowflakeDbSchema;
            this.existingSnowflakeStorageIntegrationNameWithSchema = "c.existingSnowflakeStorageIntegrationNameWithSchema"; //TOBEADDED PROPERLY;
            this.azureDataLakeFileFormatName = "CSV";
            this.azureDatalakeRootFolder = c.synapseOptions.location;
            // Value would be something like dynamics365_financeandoperations_xxxx_tst_sandbox_EDS
            this.snowflakeExternalStageName = c.synapseOptions.external_data_source;

            // Value would be something like dynamics365_financeandoperations_xxxx_tst_sandbox_FF
            this.snowflakeFileFormatName = c.synapseOptions.fileFormatName;

            this.logger = logger;
            this.conn = new SnowflakeDbConnection();
            this.conn.ConnectionString = this.SnowflakeConnectionStr;

            this.conn.Open();
        }

        ~SnowflakeHandler()
        {
            if (this.conn != null && this.conn.State != ConnectionState.Closed)
                this.conn.Close();
        }

        /// <summary>
        /// Entry point for this class, used by the console app.
        /// </summary>
        /// <param name="c"></param>
        /// <param name="metadataList"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public static SQLStatements executeSnowflake(AppConfigurations c, List<SQLMetadata> metadataList, ILogger logger)
        {
            SnowflakeHandler handler = new SnowflakeHandler(
                    c,
                    logger);

            // prep DB
            var setupStatements = handler.dbSetup();

            // convert metadata to DDL
            var statementsList = handler.sqlMetadataToDDL(metadataList, logger);
            SQLStatements statements = new SQLStatements { Statements = statementsList.Result };

            // Execute DDL
            logger.Log(LogLevel.Information, "Executing DDL");
            try
            {
                handler.executeStatements(setupStatements);
                handler.executeStatements(statements);
            }
            catch (Exception e)
            {
                logger.Log(LogLevel.Error, "ERROR executing SQL");
                foreach (var statement in statements.Statements)
                {
                    logger.LogError(statement.Statement);
                }
                logger.Log(LogLevel.Error, e.Message);
            }
            finally
            {
                ////TODO : Log stats by tables , entity , created or failed
                //SQLHandler.missingTables(c, metadataList, log);
            }
            return statements;
        }

        private SQLStatements dbSetup()
        {
            var sqldbprep = new List<SQLStatement>();

            // Create schema
            string template = @"CREATE TRANSIENT SCHEMA IF NOT EXISTS {0}";
            string statement = string.Format(template, this.dbSchema);
            sqldbprep.Add(new SQLStatement { EntityName = "CreateSchema", Statement = statement });

            // Create file format
            template = @"CREATE OR REPLACE FILE FORMAT {0}.{1} TYPE = {2}";
            statement = string.Format(template, this.dbSchema, this.snowflakeFileFormatName, this.azureDataLakeFileFormatName);
            sqldbprep.Add(new SQLStatement { EntityName = "CreateExternalStage", Statement = statement });

            // Create external stage
            template = @"CREATE OR REPLACE STAGE {0}.{1}
                STORAGE_INTEGRATION = {2}
                URL = {3}
                FILE_FORMAT = {0}.{4}
                ";
            statement = string.Format(template,
                this.dbSchema,
                this.snowflakeExternalStageName,
                this.existingSnowflakeStorageIntegrationNameWithSchema,
                this.azureDatalakeRootFolder.Replace("https", "azure"),
                this.snowflakeFileFormatName
                );
            sqldbprep.Add(new SQLStatement { EntityName = "CreateExternalStage", Statement = statement });

            return new SQLStatements { Statements = sqldbprep };

        }

        private void executeStatements(SQLStatements sqlStatements)
        {
            try
            {
                if(this.conn.State != ConnectionState.Open)
                    conn.Open();

                foreach (var s in sqlStatements.Statements)
                {
                    try
                    {
                        if (s.EntityName != null)
                        {
                            logger.LogInformation($"Executing DDL:{s.EntityName}");
                        }

                        logger.LogDebug($"Statement:{s.Statement}");
                        this.executeStatement(s.Statement);

                        logger.LogInformation($"Status:success");
                        s.Created = true;
                    }
                    catch (SnowflakeDbException ex)
                    {
                        logger.LogError($"Statement:{s.Statement}");
                        logger.LogError(ex.Message);
                        logger.LogError($"Status:failed");
                        s.Created = false;
                        s.Detail = ex.Message;
                    }
                }
            }
            catch (SnowflakeDbException e)
            {
                logger.LogError($"Connection error:{ e.Message}");
            }
        }

        public async Task<List<SQLStatement>> sqlMetadataToDDL(List<SQLMetadata> metadataList, ILogger logger)
        {
            List<SQLStatement> sqlStatements = new List<SQLStatement>();

            string templateCreateTable = "";
            string templateCreateStoredProcedure = "";

            templateCreateTable = @"CREATE OR REPLACE TRANSIENT TABLE {0}.{1} ({2})";
            templateCreateStoredProcedure = @"CREATE OR REPLACE PROCEDURE {0}.{1} ({2})";

            logger.LogInformation($"Metadata to DDL as table");

            foreach (SQLMetadata metadata in metadataList)
            {
                string sql = "";
                string dataLocation = null;

                if (string.IsNullOrEmpty(metadata.viewDefinition))
                {
                    if (metadata.columnAttributes == null || metadata.dataLocation == null)
                    {
                        logger.LogError($"Table/Entity: {metadata.entityName} invalid definition.");
                        continue;
                    }

                    logger.LogInformation($"Table:{metadata.entityName}");
                    string columnDefSQL = string.Join(", ", metadata.columnAttributes.Select(i => SnowflakeHandler.attributeToSQLType((ColumnAttribute)i)));

                    sql = string.Format(templateCreateTable,
                                         this.dbSchema, //0 
                                         metadata.entityName, //1
                                         columnDefSQL //2
                                          );
                    dataLocation = metadata.dataLocation;
                }
                else
                {
                    // Ignore entity for now.
                    //logger.LogInformation($"Entity:{metadata.entityName}");
                    //sql = TSqlSyntaxHandler.finalTsqlConversion(metadata.viewDefinition, "sql", c.synapseOptions);
                }

                if (sql != "")
                {
                    if (sqlStatements.Exists(x => x.EntityName.ToLower() == metadata.entityName.ToLower()))
                        continue;
                    else
                        sqlStatements.Add(new SQLStatement() { EntityName = metadata.entityName, DataLocation = dataLocation, Statement = sql });
                }
            }

            logger.LogInformation($"Tables:{sqlStatements.FindAll(a => a.DataLocation != null).Count}");
            logger.LogInformation($"Entities/Views:{sqlStatements.FindAll(a => a.DataLocation == null).Count}");
            return sqlStatements;
        }

        public static string attributeToColumnNames(ColumnAttribute attribute)
        {
            return $"{attribute.name}";
        }

        private void executeStatement(string query)
        {
            IDbCommand cmd = conn.CreateCommand();
            cmd.CommandText = query;
            cmd.ExecuteNonQuery();
        }

        public static string attributeToSQLType(ColumnAttribute attribute)
        {
            string sqlColumnDef;

            switch (attribute.dataType.ToLower())
            {
                case "string":
                    sqlColumnDef = $"{attribute.name} VARCHAR";
                    break;
                case "decimal":
                case "double":
                    sqlColumnDef = $"{attribute.name} ({attribute.precision} , {attribute.scale})";
                    break;
                case "biginteger":
                case "int64":
                case "bigint":
                    sqlColumnDef = $"{attribute.name} bigInt";
                    break;
                case "smallinteger":
                case "int":
                case "int32":
                case "time":
                    sqlColumnDef = $"{attribute.name} int";
                    break;
                case "date":
                case "datetime":
                case "datetime2":
                    sqlColumnDef = $"{attribute.name} TIMESTAMP_NTZ";
                    break;
                case "boolean":
                    sqlColumnDef = $"{attribute.name} BOOLEAN";
                    break;
                case "guid":
                    sqlColumnDef = $"{attribute.name} VARCHAR";
                    break;
                    case "binary":
                    sqlColumnDef = $"{attribute.name} BINARY";
                    break;

                default:
                    sqlColumnDef = $"{attribute.name} VARCHAR";
                    break;
            }

            return sqlColumnDef;
        }
    }
}
