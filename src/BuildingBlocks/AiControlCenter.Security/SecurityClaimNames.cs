namespace AiControlCenter.Security;

//Xарактеристика користувача, записана всередині JWT.

public static class SecurityClaimNames
{
    public const string Role = "role";
    //показує призначення токена.
    public const string TokenUse = "token_use";
    //Показує, чи повинен користувач змінити тимчасовий пароль
    public const string PasswordChangeRequired = "pwd_change_required";
    public const string AccessTokenUse = "access";
}
