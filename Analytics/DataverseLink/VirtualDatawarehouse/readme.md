# Setup instructions 

Step 1: Create a new database in Synapse Serverless SQL pool and master key [CreateSourceDatabaseAndMasterKey](/Analytics/DataverseLink/EDL_To_SynapseLinkDV_DBSetup/Step0_EDL_To_SynapseLinkDV_CreateSourceDatabaseAndMasterKey.sql)

Step 2: Run the setup script on the database created in step 1. This will create stored procedure and functions in the database [CreateUpdate_SetupScript](/Analytics/DataverseLink/EDL_To_SynapseLinkDV_DBSetup/Step1_EDL_To_SynapseLinkDV_CreateUpdate_SetupScript.sql)

Step 3: Run the following script to create openrowset views for all the delta tables on the Synapse serverless database [SynapseLinkDV_DataVirtualization](/Analytics/DataverseLink/VirtualDatawarehouse/Step2_1_EDL_To_SynapseLinkDV_DataVirtualization.sql)
