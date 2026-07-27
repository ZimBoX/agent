namespace ClaudeAssistant.Api.Services.Pricing;

// Считает стоимость запроса в рублях по токенам и ставкам, заданным пользователем
// (агрегатор возвращает свою стоимость в $ в поле usage.cost — её мы храним
// отдельно как факт, а это — учётная оценка по рублёвым ставкам компании).
//
// Ставки — ₽ за 1 млн токенов, задаются в конфиге (секция Pricing), с дефолтами:
//   claude-sonnet-5 — вход 400, выход 2000
//   claude-haiku-4.5 — вход 200, выход 1000
// Кэш-чтение Anthropic стоит ~10% от входа — считаем его по ставке входа × 0.1,
// иначе для документо-тяжёлого потока (Sonnet кэширует файлы) расход завышается в разы.
public class PricingService(IConfiguration configuration)
{
    private record Rate(decimal InputPerMillion, decimal OutputPerMillion);

    private static readonly Dictionary<string, Rate> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-sonnet-5"] = new(400m, 2000m),
        ["claude-haiku-4.5"] = new(200m, 1000m),
    };

    private const decimal FallbackInput = 400m;
    private const decimal FallbackOutput = 2000m;

    // Множитель ставки входа для токенов кэш-чтения (Anthropic: cache read ≈ 0.1× input).
    private const decimal CacheReadMultiplier = 0.1m;

    private Rate RateFor(string? model)
    {
        if (model is not null)
        {
            // Конфиг переопределяет дефолты: Pricing:Models:<model>:InputPerMillion/OutputPerMillion.
            var input = configuration.GetValue<decimal?>($"Pricing:Models:{model}:InputPerMillion");
            var output = configuration.GetValue<decimal?>($"Pricing:Models:{model}:OutputPerMillion");
            if (input is not null && output is not null)
            {
                return new Rate(input.Value, output.Value);
            }
            if (Defaults.TryGetValue(model, out var known))
            {
                return known;
            }
        }
        return new Rate(FallbackInput, FallbackOutput);
    }

    // Стоимость в рублях. inputTokens — «свежий» вход (+ запись кэша), cacheReadTokens —
    // чтение из кэша (по льготной ставке), outputTokens — сгенерированный ответ.
    public decimal RubCost(string? model, long inputTokens, long cacheReadTokens, long outputTokens)
    {
        var rate = RateFor(model);
        return (
            inputTokens * rate.InputPerMillion
            + cacheReadTokens * rate.InputPerMillion * CacheReadMultiplier
            + outputTokens * rate.OutputPerMillion
        ) / 1_000_000m;
    }
}
