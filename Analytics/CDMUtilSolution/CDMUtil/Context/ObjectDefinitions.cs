﻿using System;
using System.Collections.Generic;
using CDMUtil.Manifest;
using Microsoft.Data.SqlClient;

namespace CDMUtil.Context.ObjectDefinitions
{
    public class AdlsContext
    {
        public string StorageAccount { get; set; }

        public string ClientAppId { get; set; }

        public string TenantId { get; set; }

        public string ClientSecret { get; set; }

        public bool MSIAuth { get; set; }

        public string SharedKey { get; set; }

        public string FileSytemName { get; set; }

        public string EnvironmentName { get; set; }

        public List<ManifestDefinition> ManifestDefinitions { get; set; }
    }
    public class EntityList
    {
        public string manifestName { get; set; } // this will store the JSON string
        public List<EntityDefinition> entityDefinitions { get; set; } // this will be the actually list. 
    }
    public class EntityDefinition
    {
        public string name { get; set; } // this will store the JSON string
        public string description { get; set; } // this will store the JSON string
        public string corpusPath { get; set; } // this will store the JSON string
        public string dataPartitionLocation { get; set; } // this will store the JSON string
        public string partitionPattern { get; set; } // this will store the JSON string
        public List<dynamic> attributes { get; set; } // this will be the actually list. 
    }

    public class Artifacts
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public string ViewName { get; set; } = string.Empty;
    }
    public class TableManifestDefinition
    {
        public string TableName { get; set; }
        public string DataLocation { get; set; }
        public string ManifestName { get; set; }
        public string ManifestLocation { get; set; }
    }
    public class ManifestDefinitions
    {
        public List<TableManifestDefinition> Tables;
        public List<ManifestDefinition> Manifests;
        public dynamic Config;
    }
    public class Table
    {
        public string TableName;
    }
    public class ManifestDefinition
    {
        public string ManifestLocation { get; set; }
        public string ManifestName { get; set; }
        public List<Table> Tables;
    }
    public class SQLStatement
    {
        public string EntityName;
        public string Statement;
        public string DataLocation;
        public string ColumnNames;
        public bool Created;
        public string Detail;
    }
    public class SQLMetadata
    {
        public string entityName;
        public string columnNames;
        public string columnDefinition;
        public string dataLocation;
        public string viewDefinition;
        public string dependentTables;
        public string dataFilePath;
        public string metadataFilePath;
        public string cdcDataFileFilePath;
        public string columnDefinitionSpark;
        public List<ColumnAttribute> columnAttributes;
    }
    public class ColumnAttribute
    {
        public string name;
        public string description;
        public string dataType;
        public int maximumLength;
        public dynamic constantValueList;
        public bool? isNullable;
        public int precision = 32;
        public int scale = 16;
    }
    public class SQLStatements
    {
        public List<SQLStatement> Statements;
    }
    public class ManifestStatus
    {
        public string ManifestName;
        public bool IsManifestCreated;
    }
    public class EntityReferenceValues
    {
        public List<EntityReference> EntityReference;
    }
    public class EntityReference
    {
        public string entityShape { get; set; }
        public List<string> constantValues { get; set; }
    }
    public class AppConfigurations
    {
        public string tenantId;
        public string rootFolder;
        public string manifestName;
        public List<ManifestDefinition> manifestDefinitions;

        public string AXDBConnectionString;
        public List<string> tableList;
        public AdlsContext AdlsContext;
        public SynapseDBOptions synapseOptions;
        public string SourceColumnProperties;
        public string ReplaceViewSyntax;
        public bool ProcessEntities;
        public string ProcessEntitiesFilePath;
        public bool ProcessSubTableSuperTables;
        public string ProcessSubTableSuperTablesFilePath;
        public int WaitAfterEventTrigger = 60000;

        //JayConsulting/JayFu/2022
        public string targetSnowflakeDbConnectionString;
        public string targetSnowflakeDbSchema;
        public string targetSnowflakeExistingStorageIntegrationNameWithSchema;

        public AppConfigurations()
        { }
        private string getManifestUri(string manifestURL)
        {
            string manifestUri = manifestURL.Replace("/resolved/", "/");

            if (manifestURL.EndsWith("manifest.cdm.json") || manifestURL.EndsWith("model.json"))
            {
                manifestUri = manifestURL;
            }
            
            else if (manifestURL.EndsWith(".cdm.json"))
            {
                Uri originalURI = new Uri(manifestUri);
                string newManifestFilePath = string.Format("{0}://{1}", originalURI.Scheme, originalURI.Authority);
                for (int i = 0; i < originalURI.Segments.Length - 1; i++)
                {
                    newManifestFilePath += originalURI.Segments[i];
                }
                manifestUri = newManifestFilePath + originalURI.Segments[originalURI.Segments.Length - 2].Trim("/".ToCharArray()) + ".manifest.cdm.json";

                string TableName = originalURI.Segments[originalURI.Segments.Length - 1].Replace(".cdm.json", "");

                if (this.tableList is null)
                {
                    this.tableList = new List<string>();
                }
                
                tableList.Add(TableName);
            }

            manifestUri = manifestUri.Replace(".blob.", ".dfs.");

            return manifestUri;
        }
        public AppConfigurations(string tenant, string manifestURL, string accessKey, string targetConnectionString = "", string ddlType = "", string targetSparkConnection = "")
        {
            this.tenantId = tenant;

            string[] manifestUrlList = manifestURL.Split(",");

            Uri firstManifest = new Uri(this.getManifestUri(manifestUrlList[0]));
            
            string storageAccount = firstManifest.Host;
            string[] segments     = firstManifest.Segments;
            int rootDirectoryPosition = (segments.Length > 3) ? 3 : 2;
            string rootDirectory = (segments.Length > 3) ? segments[1] + segments[2] : segments[1];
            string environmentName = rootDirectory.Replace(".operations.dynamics.com", "").Replace("/", "_").Replace("-", "_").Replace(".", "_");
            environmentName = environmentName.Trim("_".ToCharArray());

            if (!String.IsNullOrEmpty(targetSparkConnection))
            {
                this.synapseOptions = new SynapseDBOptions(targetSparkConnection, environmentName);
            }
            else
            {
                string rootLocation = string.Format("{0}://{1}", firstManifest.Scheme, firstManifest.Authority) + "/" + rootDirectory;
                this.synapseOptions = new SynapseDBOptions(targetConnectionString, environmentName, rootLocation, ddlType);
            }

            this.AdlsContext = new AdlsContext()
            {
                StorageAccount = storageAccount,
                FileSytemName = rootDirectory,
                MSIAuth = String.IsNullOrEmpty(accessKey) ? true : false,
                TenantId = tenant,
                SharedKey = accessKey,
                EnvironmentName = environmentName
            };
            
            List <ManifestDefinition> mds = new List<ManifestDefinition>();

            foreach (string manifest in manifestUrlList)
            {
                Uri manifestURI = new Uri(this.getManifestUri(manifest));
                var uriSegments = manifestURI.Segments;
                this.rootFolder = String.Join("", uriSegments[new Range(rootDirectoryPosition, uriSegments.Length - 1)]);
                this.manifestName = uriSegments[uriSegments.Length - 1];

                mds.Add(
                    new ManifestDefinition{
                        ManifestLocation = String.Join("", uriSegments[new Range(rootDirectoryPosition, uriSegments.Length - 1)]),
                    ManifestName = uriSegments[uriSegments.Length - 1]
                        });
            }
            this.AdlsContext.ManifestDefinitions = mds; 

        }
    }
    public class SynapseDBOptions
    {
        public string targetDbConnectionString;
        public string masterDbConnectionString;
        public string servername;
        public string dbName;
        public string targetSparkEndpoint;
        public string targetSparkPool;
        public string masterKey = Guid.NewGuid().ToString();
        public string credentialName;
        public bool servicePrincipalBasedAuthentication;
        public string servicePrincipalTenantId;
        public string servicePrincipalAppId;
        public string servicePrincipalSecret;
        public string external_data_source;
        public string location;
        public string fileFormatName;
        public string DDLType = "SynapseView";
        public string schema = "dbo";
        public int DefaultStringLength = 1000;
        public bool TranslateEnum = false;
        public bool createStats = false;
        public string parserVersion = "2.0";
        public bool serverless = true;
       
        public SynapseDBOptions()
        { }
        public SynapseDBOptions(string sparkConnection, string environmentName)
        {
            var sparkConfig = sparkConnection.Split("@");
            if (sparkConfig.Length >= 2)
            {
                targetSparkEndpoint = sparkConfig[0];
                targetSparkPool = sparkConfig[1];
                dbName = sparkConfig.Length > 2 ? sparkConfig[2] : environmentName;
            }
        }

        public SynapseDBOptions(string targetDBConnectionString, string environmentName, string rootLocation, string ddlType)
        {

            SqlConnectionStringBuilder connectionStringBuilder;
            try
            {
                connectionStringBuilder = new SqlConnectionStringBuilder(targetDBConnectionString);
            }
            catch (KeyNotFoundException e)
            {
                Console.WriteLine(e.Message);
                throw new Exception("error");
            }
            servername = connectionStringBuilder.DataSource;
            if (String.IsNullOrEmpty(connectionStringBuilder.InitialCatalog))
            {
                connectionStringBuilder.InitialCatalog = environmentName;
                dbName = connectionStringBuilder.InitialCatalog;
                targetDbConnectionString = connectionStringBuilder.ConnectionString;
            }
            else
            {
                servername = connectionStringBuilder.DataSource;
                dbName = connectionStringBuilder.InitialCatalog;
                targetDbConnectionString = connectionStringBuilder.ConnectionString;
            }

            external_data_source = $"{environmentName}_EDS";           
            fileFormatName = $"{environmentName}_FF";
            masterKey = environmentName;
            credentialName = environmentName;
            location = rootLocation;
            // default Synapse Serverless settings 
            if (connectionStringBuilder.DataSource.Contains("-ondemand.sql.azuresynapse.net"))
            {
                serverless = true;
            }
            else
            {
                parserVersion = "1.0";
                serverless = false;
            }
            if (ddlType != null)
                DDLType = ddlType;
            if (DDLType == "SynapseTable")
            {
                fileFormatName = $"CSV";
            }

            connectionStringBuilder.InitialCatalog = "master";
            masterDbConnectionString = connectionStringBuilder.ConnectionString;

        }
    }
}
