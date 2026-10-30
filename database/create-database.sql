-- Run in MySQL Workbench or the mysql command-line client as a database administrator.
-- For local development. Change the password here and in appsettings.Local.json together.
CREATE DATABASE IF NOT EXISTS `StockvelSync_V2` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS 'StockvelSync_V2'@'localhost' IDENTIFIED BY 'change-this-local-db-password';
GRANT ALL PRIVILEGES ON `StockvelSync_V2`.* TO 'StockvelSync_V2'@'localhost';
-- All application SQL is MySQL. No SQL Server or SQLite dependency is used.
