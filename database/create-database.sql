-- Run in MySQL Workbench or the mysql command-line client as a database administrator.
-- For local development. Change the password here and in appsettings.Local.json together.
CREATE DATABASE IF NOT EXISTS stokvelsync CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS 'stokvelsync'@'localhost' IDENTIFIED BY 'change-this-local-db-password';
GRANT ALL PRIVILEGES ON stokvelsync.* TO 'stokvelsync'@'localhost';
-- All application SQL is MySQL. No SQL Server or SQLite dependency is used.
