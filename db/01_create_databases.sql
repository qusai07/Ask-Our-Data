-- Dev reset: drops and recreates both databases. Run as a sysadmin (Windows auth).
USE master;
GO
IF DB_ID(N'AskData_Reporting') IS NOT NULL
BEGIN
    ALTER DATABASE AskData_Reporting SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE AskData_Reporting;
END
GO
IF DB_ID(N'AskData_Ops') IS NOT NULL
BEGIN
    ALTER DATABASE AskData_Ops SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE AskData_Ops;
END
GO
CREATE DATABASE AskData_Reporting;
GO
CREATE DATABASE AskData_Ops;
GO
