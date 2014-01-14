﻿namespace Testeroids.Aspects
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Reflection;

    using JetBrains.Annotations;

    using NUnit.Framework;

    using PostSharp.Aspects;
    using PostSharp.Aspects.Advices;
    using PostSharp.Extensibility;

    using Testeroids.Aspects.Attributes;

    /// <summary>
    ///   <see cref="ArrangeActAssertAspectAttribute" /> provides behavior that is necessary for a better integration of AAA syntax with the unit testing framework.
    ///   Specifically, it injects calls to prerequisite tests (marked with <see cref="PrerequisiteAttribute"/>) and to the Because() method into each test in each test fixture.
    ///   It also handles assert failures so that a failing test marked as <see cref="PrerequisiteAttribute"/> is flagged as such in the exception message.
    /// </summary>
    [Serializable]
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    [MulticastAttributeUsage(MulticastTargets.Class,
        TargetTypeAttributes = MulticastAttributes.AnyScope | MulticastAttributes.AnyVisibility | MulticastAttributes.NonAbstract | MulticastAttributes.Managed,
        AllowMultiple = false, Inheritance = MulticastInheritance.Strict)]
    public class ArrangeActAssertAspectAttribute : InstanceLevelAspect
    {
        #region Constants

        /// <summary>
        /// The <see cref="BindingFlags"/> used to find test methods in a given <see cref="Type"/>.
        /// </summary>
        private const BindingFlags TestMethodBindingFlags = BindingFlags.Instance | BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.FlattenHierarchy;

        #endregion

        #region Fields

        /// <summary>
        ///   Field bound at runtime to a delegate of the method <c>Because</c> .
        /// </summary>
        [NotNull]
        [SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1401:FieldsMustBePrivate",
            Justification = "Reviewed. PostSharp requires this to be public.")]
        [ImportMember("OnBecauseRequested", IsRequired = true)]
        [UsedImplicitly]
        public Action OnBecauseRequestedMethod;

        /// <summary>
        ///   Field bound at runtime to a delegate of the method <c>RunPrerequisiteTests</c> .
        /// </summary>
        [NotNull]
        [SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1401:FieldsMustBePrivate",
            Justification = "Reviewed. PostSharp requires this to be public.")]
        [ImportMember("RunPrerequisiteTests", IsRequired = true)]
        [UsedImplicitly]
        public Action RunPrerequisiteTestsMethod;

        #endregion

        #region Constructors and Destructors

        /// <summary>
        ///   Initializes a new instance of the <see cref="ArrangeActAssertAspectAttribute" /> class.
        /// </summary>
        public ArrangeActAssertAspectAttribute()
        {
            this.AttributeTargetMemberAttributes = MulticastAttributes.Public | MulticastAttributes.Instance;
            this.AttributeTargetElements = MulticastTargets.Class;
        }

        #endregion

        #region Public Methods and Operators

        /// <summary>
        /// Checks whether the type is a candidate for the aspect to apply to it.
        /// </summary>
        /// <param name="type">the type to check if it needs the attribute.</param>
        /// <returns><c>true</c> if the attribute needs to be applied to the type; <c>false</c> otherwise.</returns>
        public override bool CompileTimeValidate(Type type)
        {
            return typeof(IContextSpecification).IsAssignableFrom(type) && base.CompileTimeValidate(type);
        }

        /// <summary>
        ///   The method executed when exception occurred in standard test execution.
        /// </summary>
        /// <param name="args"> The Method Execution Args. </param>
        [OnMethodExceptionAdvice(Master = @"OnStandardTestMethodEntry")]
        public void OnException(MethodExecutionArgs args)
        {
            if (!(args.Exception is AssertionException))
            {
                return;
            }

            if (!args.Exception.Message.TrimStart().StartsWith("Expected"))
            {
                return;
            }

            var message = string.Format("{0}.{1}\r\n{2}", args.Instance.GetType().Name, args.Method.Name, args.Exception.Message);

            if (args.Method.IsDefined(typeof(PrerequisiteAttribute), false))
            {
                message = string.Format("Prerequisite failed: {0}", message);
                args.Exception = new PrerequisiteFailureException(message, args.Exception);
                args.FlowBehavior = FlowBehavior.ThrowException;
            }
            else
            {
                args.FlowBehavior = FlowBehavior.RethrowException;
            }
        }

        /// <summary>
        ///   Executed when <see cref="Testeroids.Aspects.Attributes.ExceptionResilientAttribute"/> is set on a test method.
        /// </summary>
        /// <param name="args"> The Method interception args. </param>
        [OnMethodInvokeAdvice]
        [MethodPointcut(@"SelectExceptionResilientTestMethods")]
        public void OnExceptionResilientTestMethodEntry(MethodInterceptionArgs args)
        {
            try
            {
                this.OnTestMethodEntry(
                                       (IContextSpecification)args.Instance,
                                       args.Method,
                                       this.OnBecauseRequestedMethod);
            }
            catch (Exception e)
            {
                // we don't care about exceptions right now
                var siblingTestMethods = GetTestMethods(args.Instance.GetType());
                var expectedExceptions =
                    siblingTestMethods.SelectMany(testMethod => testMethod.GetCustomAttributes(typeof(ExpectedExceptionAttribute), false))
                                      .Cast<ExpectedExceptionAttribute>()
                                      .Select(attr => attr.ExpectedException)
                                      .Distinct()
                                      .ToArray();

                if (!expectedExceptions.Contains(null) && expectedExceptions.All(exceptionTypeToIgnore => !exceptionTypeToIgnore.IsInstanceOfType(e)))
                {
                    throw;
                }
            }
            finally
            {
                // Actual assertion.
                args.Proceed();

                // a test method has a void return type, but the documentation states that the Returnvalue must be set
                args.ReturnValue = null;
            }
        }

        /// <summary>
        ///   The method called when a method is marked with test.
        /// </summary>
        /// <param name="args"> The method execution args. </param>
        [OnMethodEntryAdvice]
        [MethodPointcut(@"SelectTestMethods")]
        [DebuggerNonUserCode]
        public void OnStandardTestMethodEntry(MethodExecutionArgs args)
        {
            this.OnTestMethodEntry((IContextSpecification)args.Instance, args.Method, this.OnBecauseRequestedMethod);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Selects all test methods in a given <paramref name="type"/>.
        /// </summary>
        /// <param name="type">
        /// The type to inspect.
        /// </param>
        /// <returns>
        /// A list of all the test methods in the specified <paramref name="type"/>.
        /// </returns>
        private static IEnumerable<MethodInfo> GetTestMethods(Type type)
        {
            var testMethods =
                from method in type.GetMethods(TestMethodBindingFlags)
                where method.IsDefined(typeof(TestAttribute), false) &&
                      !method.IsDefined(typeof(DoNotCallBecauseMethodAttribute), false)
                select method;
            return testMethods;
        }

        /// <summary>
        ///   Select the test methods marked with <see cref="Testeroids.Aspects.Attributes.ExceptionResilientAttribute"/>.
        /// </summary>
        /// <param name="type"> The test fixture type to investigate. </param>
        /// <returns> The list of test method marked with <see cref="Testeroids.Aspects.Attributes.ExceptionResilientAttribute"/>. </returns>
        [UsedImplicitly]
        private static IEnumerable<MethodBase> SelectExceptionResilientTestMethods(Type type)
        {
            var testMethods = GetTestMethods(type);
            var nestedTestMethods = type.GetNestedTypes().SelectMany(t => t.GetMethods(TestMethodBindingFlags));
            var expectedExceptionTestMethods =
                from testMethod in GetTestMethods(type).Concat(nestedTestMethods).Distinct()
                where testMethod.IsDefined(typeof(ExpectedExceptionAttribute), false)
                select testMethod;

            // If there is any test method marked with ExpectedExceptionAttribute, then all other act as if marked with ExceptionResilientAttribute
            if (expectedExceptionTestMethods.Any())
            {
                return testMethods.Except(expectedExceptionTestMethods);
            }

            // Otherwise, take only the ones actually marked with ExceptionResilientAttribute
            var selectedTestMethods =
                from testMethod in testMethods
                where testMethod.IsDefined(typeof(ExceptionResilientAttribute), true)
                select testMethod;
            return selectedTestMethods;
        }

        /// <summary>
        ///   Select the test methods marked with <see cref="TestAttribute"/>, but not marked with <see cref="Testeroids.Aspects.Attributes.DoNotCallBecauseMethodAttribute"/> or <see cref="Testeroids.Aspects.Attributes.ExceptionResilientAttribute"/>.
        /// </summary>
        /// <param name="type"> The test fixture type to investigate. </param>
        /// <returns> The list of test methods which match the prerequisites. </returns>
        [UsedImplicitly]
        private static IEnumerable<MethodBase> SelectTestMethods(Type type)
        {
            var testMethods = GetTestMethods(type);

            return testMethods.Except(SelectExceptionResilientTestMethods(type));
        }

        /// <summary>
        ///   Method executed when entering a test method.
        /// </summary>
        /// <param name="instance"> The instance of the context specification. </param>
        /// <param name="methodInfo"> The test method. </param>
        /// <param name="becauseAction"> The because method. </param>
        private void OnTestMethodEntry(
            IContextSpecification instance,
            MethodBase methodInfo,
            Action becauseAction)
        {
            var isRunningInTheContextOfAnotherTest = instance.ArePrerequisiteTestsRunning;

            if (isRunningInTheContextOfAnotherTest)
            {
                return;
            }

            var isRunningPrerequisite = methodInfo.IsDefined(typeof(PrerequisiteAttribute), true);

            becauseAction();

            if (!isRunningPrerequisite)
            {
                this.RunPrerequisiteTestsMethod();
            }
        }

        #endregion
    }
}