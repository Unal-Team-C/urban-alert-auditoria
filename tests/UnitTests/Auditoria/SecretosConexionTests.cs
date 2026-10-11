using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Infrastructure.Configuracion;
using Microsoft.Extensions.Configuration;
using Moq;

namespace UnitTests.Auditoria;

// Plan D-008: en la nube las funciones solo reciben el ARN del secreto; la cadena de conexión se lee al arrancar.
public sealed class SecretosConexionTests
{
    private const string ArnEscritor = "arn:aws:secretsmanager:us-east-2:348364742283:secret:urban-alert-auditoria-db-writer-AbCdEf";

    private static Mock<IAmazonSecretsManager> Cliente(string valor)
    {
        Mock<IAmazonSecretsManager> cliente = new();
        cliente.Setup(c => c.GetSecretValueAsync(It.Is<GetSecretValueRequest>(r => r.SecretId == ArnEscritor), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetSecretValueResponse { SecretString = valor });
        return cliente;
    }

    [Fact]
    public void ConArn_ExponeLaCadenaDeConexionDelSecreto()
    {
        Mock<IAmazonSecretsManager> cliente = Cliente(
            "{\"username\":\"audit_writer\",\"password\":\"x\",\"connection_string\":\"Host=db;Username=audit_writer;Password=x\"}");

        IConfiguration configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:AuditWriterSecretArn"] = ArnEscritor })
            .AddSecretosConexion(() => cliente.Object)
            .Build();

        Assert.Equal("Host=db;Username=audit_writer;Password=x", configuracion.GetConnectionString("AuditWriter"));
    }

    [Fact]
    public void SinArn_NoLlamaASecretsManagerYConservaLaCadenaLocal()
    {
        bool creado = false;

        IConfiguration configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:AuditWriter"] = "Host=localhost" })
            .AddSecretosConexion(() => { creado = true; return Mock.Of<IAmazonSecretsManager>(); })
            .Build();

        Assert.False(creado);
        Assert.Equal("Host=localhost", configuracion.GetConnectionString("AuditWriter"));
    }

    [Theory]
    [InlineData("{\"username\":\"audit_writer\",\"password\":\"contrasena-secreta\"}")]
    [InlineData("contrasena-secreta")]
    public void SecretoSinCadena_FallaSinMostrarSuContenido(string valor)
    {
        Mock<IAmazonSecretsManager> cliente = Cliente(valor);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:AuditWriterSecretArn"] = ArnEscritor })
            .AddSecretosConexion(() => cliente.Object)
            .Build());

        Assert.DoesNotContain("contrasena-secreta", error.ToString());
        Assert.Contains("AuditWriter", error.Message);
    }
}
