CREATE LOGIN [report_reader] WITH PASSWORD = {{REPORT_READER_PASSWORD}};
CREATE LOGIN [sales_app] WITH PASSWORD = {{SALES_APP_PASSWORD}};
CREATE SERVER ROLE [bacpac_spike_server_role];
ALTER SERVER ROLE [bacpac_spike_server_role] ADD MEMBER [sales_app];
GRANT VIEW DEFINITION ON SERVER ROLE::[bacpac_spike_server_role] TO [sales_app];
GO
CREATE USER [report_reader] FOR LOGIN [report_reader];
CREATE USER [sales_app] FOR LOGIN [sales_app];
CREATE USER [no_login_control] WITHOUT LOGIN;
EXEC(N'CREATE SCHEMA [reports] AUTHORIZATION [report_reader]');

CREATE TABLE dbo.Orders (Id int NOT NULL PRIMARY KEY);
INSERT dbo.Orders (Id) VALUES (1), (2), (3);
GRANT SELECT ON dbo.Orders TO [sales_app], [no_login_control];
CREATE ROLE [order_readers];
ALTER ROLE [order_readers] ADD MEMBER [sales_app];
