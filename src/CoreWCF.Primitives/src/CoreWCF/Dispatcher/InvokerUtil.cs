// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

using CoreWCF.Description;

namespace CoreWCF.Dispatcher
{
    internal delegate object InvokeDelegate(object target, object[] inputs, object[] outputs);

    internal delegate IAsyncResult InvokeBeginDelegate(object target, object[] inputs, AsyncCallback asyncCallback, object state);

    internal delegate object InvokeEndDelegate(object target, object[] outputs, IAsyncResult result);

    internal delegate object CreateInstanceDelegate();

    internal static class InvokerUtil
    {
        private const BindingFlags DefaultBindingFlags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
        //private readonly CriticalHelper _helper;

        //public InvokerUtil()
        //{
        //    _helper = new CriticalHelper();
        //}

        internal static bool HasDefaultConstructor(Type type)
        {
            return type.GetConstructor(DefaultBindingFlags, null, Type.EmptyTypes, null) != null;
        }

        internal static CreateInstanceDelegate GenerateCreateInstanceDelegate(Type type)
        {
            return CriticalHelper.GenerateCreateInstanceDelegate(type);
        }

        internal static InvokeDelegate GenerateInvokeDelegate(MethodInfo method, out int inputParameterCount,
            out int outputParameterCount)
        {
            return CriticalHelper.GenerateInvokeDelegate(method, out inputParameterCount, out outputParameterCount);
        }

        internal static InvokeBeginDelegate GenerateInvokeBeginDelegate(MethodInfo method, out int inputParameterCount)
        {
            return CriticalHelper.GenerateInvokeBeginDelegate(method, out inputParameterCount);
        }

        internal static InvokeEndDelegate GenerateInvokeEndDelegate(MethodInfo method, out int outputParameterCount)
        {
            return CriticalHelper.GenerateInvokeEndDelegate(method, out outputParameterCount);
        }

        private static class CriticalHelper
        {
            internal static CreateInstanceDelegate GenerateCreateInstanceDelegate(Type type)
            {
                if (type.GetTypeInfo().IsValueType)
                {
                    MethodInfo method = typeof(CriticalHelper).GetMethod(nameof(CreateInstanceOfStruct),
                    BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo generic = method.MakeGenericMethod(type);
                    return (CreateInstanceDelegate)generic.CreateDelegate(typeof(CreateInstanceDelegate));
                }
                else
                {
                    MethodInfo method = typeof(CriticalHelper).GetMethod(nameof(CreateInstanceOfClass),
                    BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo generic = method.MakeGenericMethod(type);
                    return (CreateInstanceDelegate)generic.CreateDelegate(typeof(CreateInstanceDelegate));
                }
            }

            internal static object CreateInstanceOfClass<T>() where T : class, new()
            {
                return new T();
            }

            internal static object CreateInstanceOfStruct<T>() where T : struct
            {
                return default(T);
            }

            internal static InvokeDelegate GenerateInvokeDelegate(MethodInfo method, out int inputParameterCount, out int outputParameterCount)
            {
                ParameterInfo[] parameters = method.GetParameters();
                bool returnsValue = method.ReturnType != typeof(void);
                int paramCount = parameters.Length;
                
                var inputParamPositions = new List<int>();
                var outputParamPositions = new List<int>();
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (ServiceReflector.FlowsIn(parameters[i]))
                    {
                        inputParamPositions.Add(i);
                    }

                    if (ServiceReflector.FlowsOut(parameters[i]))
                    {
                        outputParamPositions.Add(i);
                    }
                }
                
                int[] inputPos = inputParamPositions.ToArray();
                int[] outputPos = outputParamPositions.ToArray();

                inputParameterCount = inputPos.Length;
                outputParameterCount = outputPos.Length;

                // TODO: Replace with expression to remove performance cost of calling delegate.Invoke.
                InvokeDelegate lambda = delegate (object target, object[] inputs, object[] outputs)
                {
                    object[] paramsLocal = null;
                    if (paramCount > 0)
                    {
                        paramsLocal = new object[paramCount];

                        for (int i = 0; i < inputPos.Length; i++)
                        {
                            paramsLocal[inputPos[i]] = inputs[i];
                        }
                    }

                    object result = null;
                    try
                    {
                        if (returnsValue)
                        {
                            result = method.Invoke(target, paramsLocal);
                        }
                        else
                        {
                            method.Invoke(target, paramsLocal);
                        }
                    }
                    catch (TargetInvocationException tie)
                    {
                        ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                    }

                    for (int i = 0; i < outputPos.Length; i++)
                    {
                        Debug.Assert(paramsLocal != null);
                        outputs[i] = paramsLocal[outputPos[i]];
                    }

                    return result;
                };

                return lambda;
            }

            internal static InvokeBeginDelegate GenerateInvokeBeginDelegate(MethodInfo methodInfo, out int inputParameterCount)
            {
                ParameterInfo[] parameters = methodInfo.GetParameters();
                bool returnsValue = methodInfo.ReturnType != typeof(void);
                int paramCount = parameters.Length;

                var inputParamPositions = new List<int>();
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (ServiceReflector.FlowsIn(parameters[i]))
                    {
                        inputParamPositions.Add(i);
                    }
                }

                int[] inputPos = inputParamPositions.ToArray();
                inputParameterCount = inputPos.Length - 2;  // Subtracting 2 to ignore callback and state

                return (target, inputs, callback, state) =>
                {
                    object[] paramsLocal = null;
                    if (paramCount > 0)
                    {
                        paramsLocal = new object[paramCount];
                        for (int i = 0; i < inputPos.Length; i++)
                        {
                            paramsLocal[inputPos[i]] = inputs[i];
                        }
                    }

                    return Task.Run(() =>
                    {
                        object resultInvoke = null;
                        try
                        {
                            if (returnsValue)
                            {
                                resultInvoke = methodInfo.Invoke(target, paramsLocal);
                            }
                            else
                            {
                                methodInfo.Invoke(target, paramsLocal);
                            }
                        }
                        catch (TargetInvocationException tie)
                        {
                            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                        }
                        return (resultInvoke, paramsLocal);
                    });
                };
            }


            public static InvokeEndDelegate GenerateInvokeEndDelegate(MethodInfo methodInfo, out int outputParameterCount)
            {
                ParameterInfo[] parameters = methodInfo.GetParameters();
                var outputParamPositions = new List<int>();
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (ServiceReflector.FlowsOut(parameters[i]))
                    {
                        outputParamPositions.Add(i);
                    }
                }

                int[] outputPos = outputParamPositions.ToArray();

                outputParameterCount = outputPos.Length;

                return (asyncResult, callback, state) =>
                {
                    Task<(object resultInvoke, object[] paramsLocal)> task = (Task<(object resultInvoke, object[] paramsLocal)>)asyncResult;
                    (object resultInvoke, object[] paramsLocal) = task.GetAwaiter().GetResult();
                    object[] outputs = new object[outputPos.Length - 1];  // Subtracting 1 to ignore state

                    for (int i = 0; i < outputPos.Length; i++)
                    {
                        Debug.Assert(paramsLocal != null);
                        outputs[i] = paramsLocal[outputPos[i]];
                    }

                    return (resultInvoke, outputs);
                };
            }
        }
    }
}
