using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace MCPForUnity.Runtime.UI
{
    /// <summary>
    /// Lightweight proxy component that allows the MCP bridge to bind UnityEvents
    /// (e.g. Button.onClick, Slider.onValueChanged) to arbitrary methods on existing components.
    /// </summary>
    public class UIEventProxy : MonoBehaviour
    {
        [Tooltip("Component that owns the method to invoke when the event fires.")]
        public Component Target;

        [Tooltip("Name of the method to call on the target component.")]
        public string MethodName;

        [Tooltip("Optional argument to pass to the target method when supported.")]
        public string Argument;

        public void Invoke()
        {
            InvokeInternal(null);
        }

        public void InvokeBool(bool value)
        {
            InvokeInternal(value);
        }

        public void InvokeFloat(float value)
        {
            InvokeInternal(value);
        }

        private void InvokeInternal(object eventValue)
        {
            if (Target == null || string.IsNullOrEmpty(MethodName))
            {
                Debug.LogWarning("[UIEventProxy] Target or method not configured.");
                return;
            }

            MethodInfo method = FindMatchingMethod(Target.GetType(), MethodName, eventValue, Argument);
            if (method == null)
            {
                Debug.LogWarning($"[UIEventProxy] Unable to find method '{MethodName}' on '{Target.GetType().FullName}'.");
                return;
            }

            object[] parameters = BuildParameterList(method, eventValue, Argument);
            try
            {
                method.Invoke(Target, parameters);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UIEventProxy] Failed to invoke {Target.GetType().Name}.{MethodName}: {ex.Message}\n{ex}");
            }
        }

        private static MethodInfo FindMatchingMethod(Type targetType, string methodName, object eventValue, string argument)
        {
            string eventString = eventValue?.ToString();
            bool hasEvent = eventValue != null;
            bool hasArgument = !string.IsNullOrEmpty(argument);

            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (MethodInfo method in targetType.GetMethods(flags))
            {
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 0)
                {
                    return method;
                }

                if (parameters.Length == 1)
                {
                    Type parameterType = parameters[0].ParameterType;

                    if (hasEvent && parameterType.IsInstanceOfType(eventValue))
                    {
                        return method;
                    }

                    if (hasEvent && TryConvert(eventString, parameterType, out _))
                    {
                        return method;
                    }

                    if (hasArgument && TryConvert(argument, parameterType, out _))
                    {
                        return method;
                    }

                    if (parameterType == typeof(string) && (hasEvent || hasArgument))
                    {
                        return method;
                    }

                    if (parameterType.IsValueType && !parameterType.IsEnum && Nullable.GetUnderlyingType(parameterType) == null && (hasEvent || hasArgument))
                    {
                        return method;
                    }
                }
            }

            return null;
        }

        private static object[] BuildParameterList(MethodInfo method, object eventValue, string argument)
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                return Array.Empty<object>();
            }

            ParameterInfo parameter = parameters[0];
            Type parameterType = parameter.ParameterType;

            if (eventValue != null && parameterType.IsInstanceOfType(eventValue))
            {
                return new[] { eventValue };
            }

            if (eventValue != null && TryConvert(eventValue.ToString(), parameterType, out object convertedFromEvent))
            {
                return new[] { convertedFromEvent };
            }

            if (!string.IsNullOrEmpty(argument) && TryConvert(argument, parameterType, out object convertedFromArgument))
            {
                return new[] { convertedFromArgument };
            }

            if (parameterType == typeof(string))
            {
                return new[] { argument ?? eventValue?.ToString() ?? string.Empty };
            }

            if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) == null)
            {
                return new[] { Activator.CreateInstance(parameterType) };
            }

            return new object[] { null };
        }

        private static bool TryConvert(string value, Type targetType, out object converted)
        {
            converted = null;
            if (targetType == typeof(string))
            {
                converted = value;
                return true;
            }

            if (targetType == typeof(bool) && bool.TryParse(value, out bool boolResult))
            {
                converted = boolResult;
                return true;
            }

            if ((targetType == typeof(int) || targetType == typeof(int?)) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intResult))
            {
                converted = intResult;
                return true;
            }

            if ((targetType == typeof(float) || targetType == typeof(float?)) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatResult))
            {
                converted = floatResult;
                return true;
            }

            if ((targetType == typeof(double) || targetType == typeof(double?)) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleResult))
            {
                converted = doubleResult;
                return true;
            }

            return false;
        }
    }
}
