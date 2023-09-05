using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using CoreWCF.Diagnostics;
using CoreWCF.Runtime;

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CoreWCF.Dispatcher
{
    public class AsyncMethodInvoker : IOperationInvoker
    {
        private InvokeBeginDelegate _invokeBeginDelegate;
        private InvokeEndDelegate _invokeEndDelegate;
        private int _inputParameterCount;
        private int _outputParameterCount;
        private string _beginMethodName;
        private string _endMethodName;

        public AsyncMethodInvoker(MethodInfo beginMethod, MethodInfo endMethod)
        {
            BeginMethod = beginMethod ?? throw DiagnosticUtility.ExceptionUtility.ThrowHelperArgumentNull(nameof(beginMethod));
            EndMethod = endMethod ?? throw DiagnosticUtility.ExceptionUtility.ThrowHelperArgumentNull(nameof(endMethod));
        }

        public MethodInfo BeginMethod { get; }

        public MethodInfo EndMethod { get; }

        public string BeginMethodName
        {
            get
            {
                if (_beginMethodName == null)
                {
                    _beginMethodName = BeginMethod.Name;
                }

                return _beginMethodName;
            }
        }

        public string EndMethodName
        {
            get
            {
                if (_endMethodName == null)
                {
                    _endMethodName = EndMethod.Name;
                }

                return _endMethodName;
            }
        }

        public object[] AllocateInputs()
        {
            EnsureIsInitialized();

            return EmptyArray<object>.Allocate(_inputParameterCount);
        }


        public object Invoke(object instance, object[] inputs, out object[] outputs)
        {
            throw new NotImplementedException();
        }

        public IAsyncResult InvokeBegin(object instance, object[] inputs, AsyncCallback callback, object state)
        {
            return InvokeAsync(instance, inputs).ToApm(callback, state);
        }

        public object InvokeEnd(object instance, out object[] outputs, IAsyncResult result)
        {
            Tuple<object, object[]> tuple = result.ToApmEnd<Tuple<object, object[]>>();
            outputs = tuple.Item2;
            return tuple.Item1;
        }

        public ValueTask<(object returnValue, object[] outputs)> InvokeAsync(object instance, object[] inputs)
        {
            // Ensure the invoker is initialized and ready to go
            EnsureIsInitialized();

            // Validate inputs and instance
            if (instance == null)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException("No service object"));
            }

            if (inputs == null && _inputParameterCount > 0)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException("Input parameters cannot be null"));
            }

            if (inputs != null && inputs.Length != _inputParameterCount)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException("Invalid number of input parameters"));
            }

            // Allocate space for the outputs
            object[] outputs = new object[_outputParameterCount];

            // Wrap the APM pattern in a ValueTask
            return new ValueTask<(object returnValue, object[] outputs)>(InvokeAsyncCore(instance, inputs, outputs));
        }

        private async Task<(object returnValue, object[] outputs)> InvokeAsyncCore(object instance, object[] inputs, object[] outputs)
        {
            // Begin the operation
            IAsyncResult asyncResult = _invokeBeginDelegate(instance, inputs, null, null);

            // End the operation
            object returnValue = _invokeEndDelegate(instance, outputs, asyncResult);

            return (returnValue, outputs);
        }



        private void EnsureIsInitialized()
        {
            if (_invokeBeginDelegate == null || _invokeEndDelegate == null)
            {
                EnsureIsInitializedCore();
            }
        }

        private void EnsureIsInitializedCore()
        {
            // Only pass locals byref because InvokerUtil may store temporary results in the byref.
            // If two threads both reference this.count, temporary results may interact.
            InvokeBeginDelegate invokeBeginDelegate = InvokerUtil.GenerateInvokeBeginDelegate(BeginMethod, out int inputParameterCount);
            _inputParameterCount = inputParameterCount;

            InvokeEndDelegate invokeEndDelegate = InvokerUtil.GenerateInvokeEndDelegate(EndMethod, out int outputParameterCount);
            _outputParameterCount = outputParameterCount;

            _invokeEndDelegate = invokeEndDelegate;
            _invokeBeginDelegate = invokeBeginDelegate;  // must set this last due to race
        }
    }
}
