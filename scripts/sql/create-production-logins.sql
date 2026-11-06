-- Run once as sa by compose.production.yaml, and safe to run again. The passwords come
-- from the MigratorPassword and AppPassword sqlcmd variables.
IF DB_ID(N'DataBridge') IS NULL
    CREATE DATABASE [DataBridge];
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'databridge_migrator')
    CREATE LOGIN [databridge_migrator] WITH PASSWORD = N'$(MigratorPassword)';
ELSE
    ALTER LOGIN [databridge_migrator] WITH PASSWORD = N'$(MigratorPassword)';

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'databridge_app')
    CREATE LOGIN [databridge_app] WITH PASSWORD = N'$(AppPassword)';
ELSE
    ALTER LOGIN [databridge_app] WITH PASSWORD = N'$(AppPassword)';
GO

USE [DataBridge];
GO

-- Migrations and the Hangfire installer change the schema, in this database only.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'databridge_migrator')
    CREATE USER [databridge_migrator] FOR LOGIN [databridge_migrator];
ALTER ROLE db_owner ADD MEMBER [databridge_migrator];

-- The running app reads and writes rows in every schema, including HangFire. It can't
-- change the schema or manage users. sp_getapplock is available to every user.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'databridge_app')
    CREATE USER [databridge_app] FOR LOGIN [databridge_app];
ALTER ROLE db_datareader ADD MEMBER [databridge_app];
ALTER ROLE db_datawriter ADD MEMBER [databridge_app];
GO
