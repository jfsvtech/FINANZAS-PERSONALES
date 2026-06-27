using System.Data;
using Npgsql;

namespace FinanzasPersonales.Web.Data;

public class Db
{
    private readonly string _cadena;

    public Db(IConfiguration config)
    {
        _cadena = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Falta ConnectionStrings:Postgres en appsettings.json");
        if (string.IsNullOrWhiteSpace(_cadena))
            throw new InvalidOperationException("ConnectionStrings:Postgres esta vacia. Configurala con una variable de entorno o en el hosting antes de publicar.");
    }

    public IDbConnection Abrir()
    {
        var con = new NpgsqlConnection(_cadena);
        con.Open();
        return con;
    }
}
