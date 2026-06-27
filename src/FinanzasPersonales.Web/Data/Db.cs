using System.Data;
using Npgsql;

namespace FinanzasPersonales.Web.Data;

public class Db
{
    private readonly string _cadena;

    public Db(IConfiguration config)
    {
        _cadena = config.GetConnectionString("Postgres")
            ?? config["DATABASE_URL"]
            ?? throw new InvalidOperationException("Falta ConnectionStrings:Postgres o DATABASE_URL en la configuracion.");
        if (string.IsNullOrWhiteSpace(_cadena))
            throw new InvalidOperationException("La conexion PostgreSQL esta vacia. Configurala con ConnectionStrings__Postgres o DATABASE_URL antes de publicar.");
        _cadena = NormalizarCadena(_cadena);
    }

    public IDbConnection Abrir()
    {
        var con = new NpgsqlConnection(_cadena);
        con.Open();
        return con;
    }

    private static string NormalizarCadena(string cadena)
    {
        cadena = cadena.Trim();
        if (!Uri.TryCreate(cadena, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
            return cadena;

        var userInfo = uri.UserInfo.Split(':', 2);
        var usuario = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(0) ?? "");
        var password = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(1) ?? "");
        var database = uri.AbsolutePath.TrimStart('/');

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = database,
            Username = usuario,
            Password = password,
            SslMode = SslMode.Require
        };

        return builder.ConnectionString;
    }
}
