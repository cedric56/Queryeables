//using Dapper;
//using System;
//using System.Collections.Generic;
//using System.Data;
//using System.Linq;
//using System.Reflection;
//using System.Text;
//using System.Threading.Tasks;

//namespace Avae.DAL;

//internal class PostgresLowercaseAdapter : ISqlAdapter
//{
//    public void AppendColumnName(StringBuilder sb, string columnName) => sb.Append(columnName.ToLowerInvariant());
//    public void AppendColumnNameEqualsValue(StringBuilder sb, string columnName) => sb.AppendFormat("{0} = @{1}", columnName.ToLowerInvariant(), columnName);

//    public int Insert(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
//    {
//        var sb = new StringBuilder();
//        sb.AppendFormat("INSERT INTO {0} ({1}) VALUES ({2})", tableName, columnList, parameterList);

//        // If no primary key then safe to assume a join table with not too much data to return
//        var propertyInfos = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
//        if (propertyInfos.Length == 0)
//        {
//            sb.Append(" RETURNING *");
//        }
//        else
//        {
//            sb.Append(" RETURNING ");
//            bool first = true;
//            foreach (var property in propertyInfos)
//            {
//                if (!first)
//                    sb.Append(", ");
//                first = false;
//                sb.Append(property.Name);
//            }
//        }

//        var results = connection.Query(sb.ToString(), entityToInsert, transaction, commandTimeout: commandTimeout);

//        // Return the key by assigning the corresponding property in the object - by product is that it supports compound primary keys
//        var id = 0;
//        foreach (var p in propertyInfos)
//        {
//            var value = ((IDictionary<string, object>)results.First())[p.Name.ToLower()];
//            p.SetValue(entityToInsert, value, null);
//            if (id == 0)
//                id = Convert.ToInt32(value);
//        }
//        return id;
//    }

//    public async Task<int> InsertAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
//    {
//        var sb = new StringBuilder();
//        sb.AppendFormat("INSERT INTO {0} ({1}) VALUES ({2})", tableName, columnList, parameterList);

//        // If no primary key then safe to assume a join table with not too much data to return
//        var propertyInfos = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
//        if (propertyInfos.Length == 0)
//        {
//            sb.Append(" RETURNING *");
//        }
//        else
//        {
//            sb.Append(" RETURNING ");
//            bool first = true;
//            foreach (var property in propertyInfos)
//            {
//                if (!first)
//                    sb.Append(", ");
//                first = false;
//                sb.Append(property.Name);
//            }
//        }

//        var results = await connection.QueryAsync(sb.ToString(), entityToInsert, transaction, commandTimeout: commandTimeout).ConfigureAwait(false);

//        // Return the key by assigning the corresponding property in the object - by product is that it supports compound primary keys
//        var id = 0;
//        foreach (var p in propertyInfos)
//        {
//            var value = ((IDictionary<string, object>)results.First())[p.Name.ToLower()];
//            p.SetValue(entityToInsert, value, null);
//            if (id == 0)
//                id = Convert.ToInt32(value);
//        }
//        return id;
//    }
//}
