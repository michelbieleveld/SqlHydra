#!/bin/bash
touch /var/opt/mssql/data/AdventureWorks.mdf
touch /var/opt/mssql/data/AdventureWorks_log.ldf

/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P Password#123 -d master -i install.sql