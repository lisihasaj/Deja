using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Deja;

/// <summary>
/// Tracks a single asynchronous write and exposes its lifecycle
/// (<see cref="IsLoading"/>, <see cref="IsError"/>, <see cref="Data"/>) as bindable state,
/// raising <see cref="INotifyPropertyChanged.PropertyChanged"/> as it advances so components
/// can re-render.
/// </summary>
/// <typeparam name="T">The type returned by the mutation.</typeparam>
public class Mutation<T> : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True while the mutation is running.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>True when the most recent execution failed.</summary>
    public bool IsError { get; private set; }

    /// <summary>The failure message of the most recent execution, when <see cref="IsError"/> is true.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>The result of the most recent successful execution.</summary>
    public T? Data { get; private set; }

    /// <summary>
    /// Runs the mutation described by <paramref name="parameters"/>. On failure the error
    /// callbacks run and an <see cref="InvalidOperationException"/> wrapping the original
    /// exception is thrown.
    /// </summary>
    public async Task Execute(MutationParameters<T> parameters)
    {
        IsError = false;
        ErrorMessage = null;

        IsLoading = true;
        OnPropertyChanged(nameof(IsLoading));

        try
        {
            if (parameters.MutationFunction is not null)
            {
                Data = await parameters.MutationFunction();
            }
            else if (parameters.VoidMutationFunction is not null)
            {
                await parameters.VoidMutationFunction();
            }
            else
            {
                throw new ArgumentException(
                    "MutationFunction or VoidMutationFunction should be provided in order to run mutation");
            }

            await InvokeSuccessCallbacks(parameters);
        }
        catch (Exception e)
        {
            Data = default;

            IsError = true;
            OnPropertyChanged(nameof(IsError));

            ErrorMessage = e.Message;
            OnPropertyChanged(nameof(ErrorMessage));

            await InvokeErrorCallbacks(parameters, e);
            throw new InvalidOperationException("Mutation failed", e);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsLoading));

            await InvokeSettledCallbacks(parameters);
        }
    }

    private async Task InvokeSuccessCallbacks(MutationParameters<T> parameters)
    {
        if (parameters.OnSuccessAsync is not null)
        {
            await parameters.OnSuccessAsync(Data);
        }

        parameters.OnSuccess?.Invoke(Data);
        OnPropertyChanged(nameof(Data));
    }

    private static async Task InvokeErrorCallbacks(MutationParameters<T> parameters, Exception e)
    {
        if (e is DisplayUserException displayUserException)
        {
            if (parameters.OnDisplayUserErrorAsync is not null)
            {
                await parameters.OnDisplayUserErrorAsync(displayUserException);
            }

            parameters.OnDisplayUserError?.Invoke(displayUserException);
        }

        if (parameters.OnErrorAsync is not null)
        {
            await parameters.OnErrorAsync(e);
        }

        parameters.OnError?.Invoke(e);
    }

    private async Task InvokeSettledCallbacks(MutationParameters<T> parameters)
    {
        if (parameters.OnSettledAsync is not null)
        {
            await parameters.OnSettledAsync(Data);
        }

        parameters.OnSettled?.Invoke(Data);
    }

    /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>Describes one execution of a <see cref="Mutation{T}"/>: what to run and which callbacks to invoke.</summary>
/// <typeparam name="T">The type returned by the mutation.</typeparam>
public class MutationParameters<T>
{
    /// <summary>The mutation to run. Takes precedence over <see cref="VoidMutationFunction"/>.</summary>
    public Func<Task<T>>? MutationFunction { get; set; }

    /// <summary>A mutation without a return value; used when <see cref="MutationFunction"/> is not set.</summary>
    public Func<Task>? VoidMutationFunction { get; set; }

    /// <summary>Callback invoked when the mutation succeeds.</summary>
    public Action<T?>? OnSuccess { get; set; }

    /// <summary>Async callback invoked when the mutation succeeds.</summary>
    public Func<T?, Task>? OnSuccessAsync { get; set; }

    /// <summary>Async callback invoked when the mutation fails.</summary>
    public Func<Exception, Task>? OnErrorAsync { get; set; }

    /// <summary>Callback invoked when the mutation fails.</summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>Async callback invoked when the mutation fails with a <see cref="DisplayUserException"/>.</summary>
    public Func<DisplayUserException, Task>? OnDisplayUserErrorAsync { get; set; }

    /// <summary>Callback invoked when the mutation fails with a <see cref="DisplayUserException"/>.</summary>
    public Action<DisplayUserException>? OnDisplayUserError { get; set; }

    /// <summary>Callback invoked when the mutation settles (success or failure).</summary>
    public Action<T?>? OnSettled { get; set; }

    /// <summary>Async callback invoked when the mutation settles (success or failure).</summary>
    public Func<T?, Task>? OnSettledAsync { get; set; }
}
