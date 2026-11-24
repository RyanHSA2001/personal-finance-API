namespace PersonalControl.Api.Data;

// Lido da seção "Database" do appsettings.json.
public class DatabaseOptions
{
    // Arquivo do banco SQLite. Caminho relativo é resolvido a partir da pasta do projeto.
    // O arquivo e a tabela são criados automaticamente na primeira execução.
    public string Path { get; set; } = "data/personalcontrol.db";
}
