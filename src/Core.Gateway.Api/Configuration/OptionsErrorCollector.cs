using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.Configuration;

internal sealed class OptionsErrorCollector
{
    private readonly List<string> _errors = [];

    /// <summary>
    /// Joins a parent and child option path.
    /// </summary>
    /// <param name="parent">Parent path.</param>
    /// <param name="child">Child name.</param>
    /// <returns>The joined path.</returns>
    public static string Path(string parent, string child) => $"{parent}:{child}";

    /// <summary>
    /// Adds an error.
    /// </summary>
    /// <param name="message">The error.</param>
    public void Add(string message) => _errors.Add(message);

    /// <summary>
    /// Adds an error under an option path.
    /// </summary>
    /// <param name="path">The option path.</param>
    /// <param name="message">The error.</param>
    public void Add(string path, string message) => _errors.Add($"{path}: {message}");

    /// <summary>
    /// Adds an error if the section is missing, otherwise validates it.
    /// </summary>
    /// <param name="section">The options section.</param>
    /// <param name="path">Its option path.</param>
    /// <returns>True when the section exists.</returns>
    public bool Require([NotNullWhen(true)] object? section, string path)
    {
        if (section is null)
        {
            _errors.Add($"{path} is required.");
            return false;
        }

        Validate(section, path);
        return true;
    }

    /// <summary>
    /// Validates the section when it exists.
    /// </summary>
    /// <param name="section">The options section, or null.</param>
    /// <param name="path">Its option path.</param>
    public void ValidateIfPresent(object? section, string path)
    {
        if (section is not null)
            Validate(section, path);
    }

    /// <summary>
    /// Builds the result.
    /// </summary>
    /// <returns>Success, or a failure listing every error.</returns>
    public ValidateOptionsResult ToResult() =>
        _errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(_errors);

    /// <summary>
    /// Runs data annotation validation on a section.
    /// </summary>
    /// <param name="section">The options section.</param>
    /// <param name="path">Its option path.</param>
    private void Validate(object section, string path)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(section, new ValidationContext(section), results, validateAllProperties: true);

        foreach (var result in results)
            Add(path, result.ErrorMessage ?? "is invalid.");
    }
}
