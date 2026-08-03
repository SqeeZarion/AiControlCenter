using Microsoft.AspNetCore.SignalR;

namespace AiControlCenter.Gateway.Hubs;

//створює SignalR Hub — точку постійного двостороннього зв’язку між Angular і Gateway.

public sealed class SystemHub : Hub
{
    // Використовується для перевірки:
    //
    // чи Angular під’єднався до SignalR;
    // чи доступний Gateway;
    // чи може клієнт викликати серверний метод;
    // чи правильно серіалізується відповідь.
    public TechnicalPong Ping() => new("gateway", DateTimeOffset.UtcNow);
}

// Це незмінна транспортна модель відповіді.
//
// Service — який сервіс відповів;
// Timestamp — час відповіді в UTC;
// record добре підходить для DTO та повідомлень.
public sealed record TechnicalPong(string Service, DateTimeOffset Timestamp);
