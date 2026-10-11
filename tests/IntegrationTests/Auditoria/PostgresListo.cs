using Npgsql;

namespace IntegrationTests.Auditoria;

// La imagen oficial de PostgreSQL arranca un servidor temporal (solo por socket local) para inicializar la base y
// después lo reinicia; la espera de Testcontainers puede terminar antes y la primera conexión TCP recibe
// «Connection reset by peer». Una conexión TCP correcta solo puede ser del servidor definitivo.
internal static class PostgresListo
{
    public static async Task EsperarAsync(string connectionString, int intentos = 60)
    {
        string sinPool = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        for (int intento = 1; ; intento++)
        {
            try
            {
                await using NpgsqlConnection connection = new(sinPool);
                await connection.OpenAsync();
                await using NpgsqlCommand command = new("SELECT 1", connection);
                await command.ExecuteScalarAsync();
                return;
            }
            catch (Exception) when (intento < intentos)
            {
                await Task.Delay(500);
            }
        }
    }
}
