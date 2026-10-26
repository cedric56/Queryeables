using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

namespace Avae.DAL;

public static class PostgreSQLExtensions
{
    //[UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "DefaultAdapter")]
    //public static extern ref ISqlAdapter GetDefaultAdapter(
    // [UnsafeAccessorType("Dapper.Contrib.Extensions.SqlMapperExtensions, Dapper.Contrib")] object? facade);

    //class PostgreIdentity : IDBIdentity
    //{
    //    public string Parse(string commandText)
    //    {
    //        return commandText.Replace("SCOPE_IDENTITY", "id");
    //    }
    //}

    public class SqlPostgreFactory : DBFactory<NpgsqlConnection>
    {
        private readonly string connectionString;

        public SqlPostgreFactory(string connectionString)
            : base(connectionString)
        {
            this.connectionString = connectionString;
        }

        public override DbConnection? CreateConnection()
        {
            var connection = new NpgsqlConnection()
            {
                ConnectionString = connectionString
            };
            return connection;
        }
    }

    public static void UseNpgsqlFactory(this IServiceCollection services,
       string connectionString)
    {
        //GetDefaultAdapter(null) = new PostgresLowercaseAdapter();

        var factory = new SqlPostgreFactory(connectionString);
        //services.AddSingleton<IDBIdentity, PostgreIdentity>();
        services.AddSingleton<IDBFactory>(sp => factory);
        services.AddTransient<IDbConnection>(_ => factory.CreateConnection()!);
    }
}
