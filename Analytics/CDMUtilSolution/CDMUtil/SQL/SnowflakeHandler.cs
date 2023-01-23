// <copyright company="JayConsulting">
// Copyright (c) 2022 All Rights Reserved
// <author>Jay Fu</author>
// https://github.com/snowflakedb/snowflake-connector-net
// </copyright>

/* TODO:
 * 1. DONE create view DONE.
 * 2. DONE sproc get table column list, get stage column list.
 * 3. create a meta table to store COPY result, and udpate sproc to insert into it.
 * 4. DONE create tasks.
 * 4a.DONE resume table tasks. MAIN TASK SHOULD BE RESUMED MANUALLY IN SNOWFLAKE.
 * 5. DONE verify csv esape charactor and text quotes.
 * 6. DONE create full reload tasks.
 * 7. DONE add columns for enum label in tables.
 */

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
        private bool snowflakeDryRun; //Log the Snowflake statement only without execution.
        private string snowflakeDBSchema;
        private string snowflakeWarehouse;
        private string snowflakeExternalStageName;
        private string snowflakeFileFormatName;
        private string snowflakeExistingStorageIntegrationNameWithSchema;
        private const string snowflakeMainTaskName = "TK_COPY_MAIN";
        private const string snowflakeNameFullReloadString = "FULL_RELOAD";
        private const string snowflakeMainTaskFullReloadName = snowflakeMainTaskName + "_" + snowflakeNameFullReloadString; // with force option in the copy commands.
        private string azureDataLakeFileFormatName;
        private string azureDatalakeRootFolder;

        public SnowflakeHandler(AppConfigurations c, ILogger logger)
        {
            this.c = c;

            this.snowflakeDryRun = true;
            this.SnowflakeConnectionStr = c.targetSnowflakeDbConnectionString;
            this.snowflakeDBSchema = c.targetSnowflakeDbSchema;
            this.snowflakeWarehouse = c.targetSnowflakeWarehouse;
            this.snowflakeExistingStorageIntegrationNameWithSchema = c.targetSnowflakeExistingStorageIntegrationNameWithSchema;
            this.azureDataLakeFileFormatName = "CSV";
            this.azureDatalakeRootFolder = c.synapseOptions.location.ToUpper();
            // Value would be something like dynamics365_financeandoperations_xxxx_tst_sandbox_EDS
            this.snowflakeExternalStageName = c.synapseOptions.external_data_source.ToUpper();

            // Value would be something like dynamics365_financeandoperations_xxxx_tst_sandbox_FF
            this.snowflakeFileFormatName = c.synapseOptions.fileFormatName.ToUpper();

            this.logger = logger;
            if (this.snowflakeDryRun == false)
            {
                this.conn = new SnowflakeDbConnection();
                this.conn.ConnectionString = this.SnowflakeConnectionStr;

                this.conn.Open();
            }
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
            string statement = string.Format(template, this.snowflakeDBSchema);
            sqldbprep.Add(new SQLStatement { EntityName = "CreateSchema", Statement = statement });

            // Create file format
            template = @"CREATE FILE FORMAT IF NOT EXISTS {0}.{1} TYPE = {2} FIELD_DELIMITER=',' FIELD_OPTIONALLY_ENCLOSED_BY='""' ENCODING='UTF-8'";
            statement = string.Format(template,
                this.snowflakeDBSchema,
                this.snowflakeFileFormatName,
                this.azureDataLakeFileFormatName
                );
            sqldbprep.Add(new SQLStatement { EntityName = "CreateFileFormat", Statement = statement });

            // Create external stage
            template = @"CREATE STAGE IF NOT EXISTS {0}.{1}
                STORAGE_INTEGRATION = {2}
                URL = '{3}'
                FILE_FORMAT = {0}.{4}
                ";
            statement = string.Format(template,
                this.snowflakeDBSchema,
                this.snowflakeExternalStageName,
                this.snowflakeExistingStorageIntegrationNameWithSchema,
                this.azureDatalakeRootFolder.Replace("HTTPS", "AZURE"),
                this.snowflakeFileFormatName
                );
            sqldbprep.Add(new SQLStatement { EntityName = "CreateExternalStage", Statement = statement });

            // Create main task
            // PLEASE RESUME THE TASK MANUALLY IN SNOWFLAKE !!!!!!!!!!
            // !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
            template = @"CREATE TASK IF NOT EXISTS {0}.{1}
WAREHOUSE = {2}
AS
SELECT CURRENT_TIMESTAMP;";
            statement = string.Format(template,
                this.snowflakeDBSchema,
                SnowflakeHandler.snowflakeMainTaskName,
                this.snowflakeWarehouse
                );
            sqldbprep.Add(new SQLStatement { EntityName = "CreateMainTask", Statement = statement });

            // task with force copy option
            statement = string.Format(template,
                this.snowflakeDBSchema,
                SnowflakeHandler.snowflakeMainTaskFullReloadName,
                this.snowflakeWarehouse
                );
            sqldbprep.Add(new SQLStatement { EntityName = "CreateMainTaskFullReload", Statement = statement });

            return new SQLStatements { Statements = sqldbprep };

        }

        private void executeStatements(SQLStatements sqlStatements)
        {
            try
            {
                if(this.conn != null && this.conn.State != ConnectionState.Open && this.snowflakeDryRun == false)
                    conn.Open();

                foreach (var s in sqlStatements.Statements)
                {
                    try
                    {
                        if (s.EntityName != null)
                        {
                            logger.LogInformation($"Executing DDL:{s.EntityName}");
                        }

                        if (this.snowflakeDryRun == false)
                        {
                            logger.LogDebug($"Statement:{s.Statement}");
                            this.executeStatement(s.Statement);
                        }
                        else
                        {
                            logger.LogInformation($"Statement:{s.Statement}"); 
                        }

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

        private void executeStatement(string query)
        {
            IDbCommand cmd = conn.CreateCommand();
            cmd.CommandText = query;
            cmd.ExecuteNonQuery();
        }

        public async Task<List<SQLStatement>> sqlMetadataToDDL(List<SQLMetadata> metadataList, ILogger logger)
        {
            List<SQLStatement> sqlStatements = new List<SQLStatement>();

            string templateCreateTable = "";
            string templateCreateStoredProcedure = "";
            string templateCreateView = "";
            string templateCreateTask = "";

            templateCreateTable = @"CREATE OR REPLACE TRANSIENT TABLE {0}.{1} ({2})";

            templateCreateStoredProcedure = @"CREATE OR REPLACE PROCEDURE {0}.{1}({6} BOOLEAN)
RETURNS INTEGER
LANGUAGE SQL
AS
DECLARE ROW_COUNT INTEGER := 0;
BEGIN
    IF ({6} = TRUE) THEN
        TRUNCATE TABLE {0}.{7};
        COPY INTO {0}.{7}({2})
        FROM (SELECT {3}, METADATA$FILENAME, METADATA$FILE_ROW_NUMBER FROM @{4}/{5} AS T)
        FORCE=TRUE;
    ELSE
        COPY INTO {0}.{7}({2})
        FROM (SELECT {3}, METADATA$FILENAME, METADATA$FILE_ROW_NUMBER FROM @{4}/{5} AS T);
    END IF;

    SELECT COUNT(1) INTO :ROW_COUNT FROM TABLE(RESULT_SCAN(LAST_QUERY_ID()));
    RETURN ROW_COUNT;
END
";

            templateCreateView = @"CREATE OR REPLACE VIEW {0}.{1}
AS
SELECT {2}
FROM {0}.{3}
QUALIFY ROW_NUMBER() OVER (PARTITION BY RECID ORDER BY DATALAKEMODIFIED_DATETIME DESC) = 1;
";

            // Task run after the main task.
            templateCreateTask = @"CREATE OR REPLACE TASK {0}.{1} WAREHOUSE = {5}
AFTER {2}
AS 
CALL {0}.{3}({4});

ALTER TASK {0}.{1} RESUME;
";

            logger.LogInformation($"Metadata to DDL as table");

            string sqlCreateTable, sqlCreateSproc, sqlCreateView, sqlCreateTask, sqlCreateTaskForce;
            string dataLocation;
            foreach (SQLMetadata metadata in metadataList)
            {
                sqlCreateTable = "";
                sqlCreateSproc = "";
                sqlCreateView = "";
                sqlCreateTask = "";
                sqlCreateTaskForce = "";
                dataLocation = null;

                if (string.IsNullOrEmpty(metadata.viewDefinition))
                {
                    if (metadata.columnAttributes == null || metadata.dataLocation == null)
                    {
                        logger.LogError($"Table/Entity: {metadata.entityName.ToUpper()} invalid definition.");
                        continue;
                    }

                    logger.LogInformation($"Table:{metadata.entityName.ToUpper()}");
                    string columnDefSQL = string.Join(", ", metadata.columnAttributes.Select(i => SnowflakeHandler.attributeToSQLType((ColumnAttribute)i)));
                    string columnNames = string.Join(", ", metadata.columnAttributes.Select(i => SnowflakeHandler.attributeToColumnNames((ColumnAttribute)i)));
                    string columnNamesView = string.Join(", ", metadata.columnAttributes.Select(i => SnowflakeHandler.attributeToViewColumnNames((ColumnAttribute)i)));
                    string columnNamesSnowfalkeStage = "";
                    for(int i = 1; i <= metadata.columnAttributes.Count; i++)
                    {
                        columnNamesSnowfalkeStage += "$" + i.ToString();
                        if (i < metadata.columnAttributes.Count)
                            columnNamesSnowfalkeStage += ", ";
                    }

                    // Add metadata columns
                    columnDefSQL += ", METADATA_FILENAME VARCHAR, METADATA_FILE_ROW_NUMBER INT, ODS_LOAD_DATETIME_UTC TIMESTAMP_NTZ DEFAULT SYSDATE()";
                    columnNames += ", METADATA_FILENAME, METADATA_FILE_ROW_NUMBER";
                    columnNamesView += ", METADATA_FILENAME, METADATA_FILE_ROW_NUMBER, ODS_LOAD_DATETIME_UTC";

                    // Create table
                    sqlCreateTable = string.Format(templateCreateTable,
                        this.snowflakeDBSchema,                                 //0 schema
                        metadata.entityName.ToUpper(),                          //1 table name
                        columnDefSQL                                            //3 column def
                        );

                    // Create sproc
                    sqlCreateSproc = string.Format(templateCreateStoredProcedure,
                        this.snowflakeDBSchema,                                 //0 schema
                        "SP_COPY_" + metadata.entityName.ToUpper(),             //1 procedure name
                        columnNames,                                            //2 table columns
                        columnNamesSnowfalkeStage,                              //3 Snowflake stage columns, e.g. $1, $2 etc...
                        this.snowflakeExternalStageName,                        //4 Snowflake stage
                        metadata.dataLocation,                                  //5 Azure location for the data files
                        SnowflakeHandler.snowflakeNameFullReloadString,         //6 object name suffix, FULL_RELOAD
                        metadata.entityName.ToUpper()                           //7 table name
                        );

                    // Create view
                    sqlCreateView = string.Format(templateCreateView,
                        this.snowflakeDBSchema,                                 //0 schema
                        metadata.entityName.ToUpper() + "_VW",                  //1 view name
                        columnNamesView,                                        //2 view column names
                        metadata.entityName.ToUpper()                           //2 table name
                        );

                    // Create task
                    sqlCreateTask = string.Format(templateCreateTask,
                        this.snowflakeDBSchema,                                 //0 schema
                        "TK_COPY_" + metadata.entityName.ToUpper(),             //1 task name
                        SnowflakeHandler.snowflakeMainTaskName,                 //2 parent task
                       "SP_COPY_" + metadata.entityName.ToUpper(),              //3 procedure name
                        "FALSE",                                                //4 FORCE option for the procedure
                        this.snowflakeWarehouse                                 //5 Snowflake warehouse
                        );

                    sqlCreateTaskForce = string.Format(templateCreateTask,
                        this.snowflakeDBSchema,
                        "TK_COPY_" + metadata.entityName.ToUpper() + "_" + snowflakeNameFullReloadString,
                        SnowflakeHandler.snowflakeMainTaskFullReloadName,
                        "SP_COPY_" + metadata.entityName.ToUpper(),
                        "TRUE",
                        this.snowflakeWarehouse
                        );

                    dataLocation = metadata.dataLocation;
                }
                else
                {
                    // Ignore entity for now.
                    //logger.LogInformation($"Entity:{metadata.entityName.ToUpper()}");
                    //sql = TSqlSyntaxHandler.finalTsqlConversion(metadata.viewDefinition, "sql", c.synapseOptions);
                }

                if (sqlCreateTable != "")
                {
                    if (sqlStatements.Exists(x => x.EntityName.ToLower() == metadata.entityName.ToUpper().ToLower()))
                        continue;
                    else
                    {
                        sqlStatements.Add(new SQLStatement() { EntityName = metadata.entityName.ToUpper() + ": Create Table", DataLocation = dataLocation, Statement = sqlCreateTable });
                        sqlStatements.Add(new SQLStatement() { EntityName = metadata.entityName.ToUpper() + ": Create Procedure", DataLocation = dataLocation, Statement = sqlCreateSproc });
                        sqlStatements.Add(new SQLStatement() { EntityName = metadata.entityName.ToUpper() + ": Create View", DataLocation = dataLocation, Statement = sqlCreateView });
                        sqlStatements.Add(new SQLStatement() { EntityName = metadata.entityName.ToUpper() + ": Create Task", DataLocation = dataLocation, Statement = sqlCreateTask });
                        sqlStatements.Add(new SQLStatement() { EntityName = metadata.entityName.ToUpper() + ": Create Forced Task", DataLocation = dataLocation, Statement = sqlCreateTaskForce });
                    }
                }
            }

            logger.LogInformation($"Tables:{sqlStatements.FindAll(a => a.DataLocation != null).Count}");
            logger.LogInformation($"Entities/Views:{sqlStatements.FindAll(a => a.DataLocation == null).Count}");
            return sqlStatements;
        }

        public static string attributeToColumnNames(ColumnAttribute attribute)
        {
            return $"{attribute.name.ToUpper()}";
        }

        public static string attributeToViewColumnNames(ColumnAttribute attribute)
        {
            string sqlColumnNames = "";
            string attributeNameModified;
            string dataTypeDefault;

            //if (attribute.isNullable == false)
            // Casting default value to NULL for non-enum fields
            if (attribute.isNullable == false)
            {
                switch (attribute.dataType.ToLower())
                {
                    case "string":
                        dataTypeDefault = "''";
                        break;
                    case "decimal":
                    case "double":
                    case "biginteger":
                    case "int64":
                    case "bigint":
                    case "smallinteger":
                    case "int":
                    case "int32":
                    case "time":
                    case "boolean":
                        dataTypeDefault = "0";
                        break;
                    case "date":
                    case "datetime":
                    case "datetime2":
                        dataTypeDefault = "'1900-01-01'";
                        break;
                    case "guid":
                        dataTypeDefault = "'00000000-0000-0000-0000-000000000000'";
                        break;
                    default:
                        dataTypeDefault = "''";
                        break;
                }

                attributeNameModified = $"COALESCE({attribute.name.ToUpper()}, {dataTypeDefault}) AS {attribute.name.ToUpper()}";
            }
            else
            {
                attributeNameModified = attribute.name.ToUpper();
            }

            // Add Enum translation
            if (attribute.dataType.ToLower() == "int32"
                && attribute.constantValueList != null)
            {
                var constantValues = attribute.constantValueList.ConstantValues;
                sqlColumnNames += $"{attributeNameModified}, CASE {attribute.name}";
                foreach (var constantValueList in constantValues)
                {
                    sqlColumnNames += $"{ " WHEN " + constantValueList[3] + " THEN '" + constantValueList[2]}'";
                }
                sqlColumnNames += $" END AS {attribute.name}_LABEL";
            }
            else
            {
                sqlColumnNames = $"{attributeNameModified}";
            }

            return sqlColumnNames;
        }

        public static string attributeToSQLType(ColumnAttribute attribute)
        {
            string sqlColumnDef;

            switch (attribute.dataType.ToLower())
            {
                case "string":
                    if (attribute.maximumLength == -1)
                    {
                        sqlColumnDef = $"{attribute.name} VARCHAR";
                    }
                    else
                    {
                        sqlColumnDef = $"{attribute.name} VARCHAR({attribute.maximumLength})";
                    }
                    break;
                case "decimal":
                case "double":
                    sqlColumnDef = $"{attribute.name} NUMBER({attribute.precision} , {attribute.scale})";
                    break;
                case "biginteger":
                case "int64":
                case "bigint":
                case "smallinteger":
                case "int":
                case "int32":
                case "time":
                    sqlColumnDef = $"{attribute.name} NUMBER";
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
                    sqlColumnDef = $"{attribute.name} VARCHAR({attribute.maximumLength})";
                    break;
            }

            sqlColumnDef += $" COMMENT '{attribute.description}'";

            return sqlColumnDef.ToUpper();
        }
    }
}
