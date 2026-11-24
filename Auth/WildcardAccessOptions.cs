namespace PersonalControl.Api.Auth;

// Acesso coringa temporário, lido da seção "WildcardAccess" do appsettings.
// Só vale quando e-mail e senha estão preenchidos. Os valores ficam apenas no
// appsettings.Development.json, então não existem fora do ambiente de desenvolvimento.
// Remover quando o cadastro de usuários estiver definitivo.
public class WildcardAccessOptions
{
    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Name { get; set; } = "Acesso coringa";

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);
}
