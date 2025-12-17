using MyRedis.Abstractions.Configuration;

namespace MyRedis.Services.Configuration;

/// <summary>
/// Concrete parameter implementation.
/// Immutable metadata + mutable current value.
/// </summary>
public class ConfigParameter : IConfigParameter
{
    public string Name { get; }
    public string Description { get; }
    public string DefaultValue { get; }
    public string CurrentValue { get; private set; }
    public Type Type { get; }
    public IParameterValidator Validator { get; }
    public bool IsHotReloadable { get; }
    public bool IsMutable { get; }
    public string Category { get; }
    public string IntroducedInVersion { get; }
    public string? DeprecationMessage { get; }

    public ConfigParameter(
        string name,
        string description,
        string defaultValue,
        Type type,
        IParameterValidator validator,
        bool isHotReloadable = true,
        bool isMutable = true,
        string category = "general",
        string introducedInVersion = "1.0",
        string? deprecationMessage = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        DefaultValue = defaultValue ?? throw new ArgumentNullException(nameof(defaultValue));
        CurrentValue = defaultValue; // Start with default
        Type = type ?? throw new ArgumentNullException(nameof(type));
        Validator = validator ?? throw new ArgumentNullException(nameof(validator));
        IsHotReloadable = isHotReloadable;
        IsMutable = isMutable;
        Category = category ?? throw new ArgumentNullException(nameof(category));
        IntroducedInVersion = introducedInVersion ?? throw new ArgumentNullException(nameof(introducedInVersion));
        DeprecationMessage = deprecationMessage;

        // Validate default value
        var validation = Validator.Validate(defaultValue);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Default value '{defaultValue}' is invalid for parameter '{name}': {validation.ErrorMessage}",
                nameof(defaultValue));
        }
    }

    /// <summary>
    /// Gets typed value with conversion.
    /// </summary>
    public T GetValue<T>()
    {
        // Handle special case for boolean (yes/no -> true/false)
        if (typeof(T) == typeof(bool))
        {
            var normalized = CurrentValue.ToLower();
            bool boolValue = normalized is "yes" or "true" or "1";
            return (T)(object)boolValue;
        }

        // Standard type conversion
        return (T)Convert.ChangeType(CurrentValue, typeof(T));
    }

    /// <summary>
    /// Sets current value (internal - called by ConfigurationService).
    /// </summary>
    internal void SetValue(string value)
    {
        CurrentValue = value ?? throw new ArgumentNullException(nameof(value));
    }
}
