﻿using Fawdlstty.SimpleMS.Attributes;
using Fawdlstty.SimpleMS.Datum;
using Fawdlstty.SimpleMS.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;

namespace Fawdlstty.SimpleMS.Private {
	internal class ImplTypeBuilder {
		private static bool s_init = true;

		// 初始化接口信息
		public static void InitInterfaces (ServiceUpdateOption _option) {
			if (s_init) {
				s_init = false;
			} else {
				throw new NotSupportedException ("请确保 services.AddSimpleMSClient () 与 services.AddSimpleMSService () 一共只被调用一次");
			}

			// 枚举所有接口
			var _types = Assembly.GetExecutingAssembly ().GetTypes ();
			foreach (var _type in _types) {
				var _type_attr = _type.GetCustomAttribute<ServiceMethodAttribute> ();
				if (_type_attr == null)
					continue;
				if (!_type.IsInterface)
					throw new TypeLoadException ("具有 [ServiceMethod] 标注的类必须为接口类型");

				// 如果需要，那么搜索本地模块
				var _type_impls = (from p in _types where p.BaseType == _type select p);
				if (_type_impls.Count () > 1)
					throw new TypeLoadException ("实现 [ServiceMethod] 标注的接口的类最多只能有一个");
				object _impl_o = null;
				if (_type_impls.Any ()) {
					// 从本地模块加载
					_impl_o = Activator.CreateInstance (_type_impls.First ());
				} else {
					// 从外部模块加载
					// 创建类生成器
					string _name = _type.FullName.Replace ('.', '_');
					var _assembly_name = new AssemblyName ($"_faw_assembly__{_name}_");
					var _assembly_builder = AssemblyBuilder.DefineDynamicAssembly (_assembly_name, AssemblyBuilderAccess.Run);
					var _module_builder = _assembly_builder.DefineDynamicModule ($"_faw_module__{_name}_");
					var _type_builder = _module_builder.DefineType ($"_faw_type__{_name}_", TypeAttributes.Public | TypeAttributes.Class);

					// 创建构造函数
					var _constructor_builder = _type_builder.DefineConstructor (MethodAttributes.Public, CallingConventions.Standard, Array.Empty<Type> ());
					_constructor_builder.GetILGenerator ().Emit (OpCodes.Ret);

					// 定义存储降级函数的结构
					var _deg_funcs = new List<Func<Dictionary<string, object>, Type, Task>> ();
					var _field_deg_funcs = _type_builder.DefineField ("_faw_field__deg_funcs_", typeof (List<Func<Dictionary<string, object>, Type, object>>), FieldAttributes.Private);

					// 定义存储返回类型的结构
					var _return_types = new List<Type> ();
					var _field_return_types = _type_builder.DefineField ("_faw_field__return_types_", typeof (List<Type>), FieldAttributes.Private);

					// 循环新增新的函数处理
					_type_builder.AddInterfaceImplementation (_type);
					var _method_infos = _type.GetMethods ();
					for (int i = 0; i < _method_infos.Length; ++i) {
						// 不允许出现getter和setter
						if (_method_infos [i].Name.StartsWith ("get_") || _method_infos [i].Name.StartsWith ("set_")) {
							throw new TypeLoadException ("具有 [ServiceMethod] 标注的接口不允许出现 getter/setter 函数 （函数名不允许 get_/set_ 开头）");
						}

						// 只提供异步函数
						if (_method_infos [i].ReturnType != typeof (Task) && _method_infos [i].ReturnType.BaseType != typeof (Task)) {
							throw new TypeLoadException ($"具有 [ServiceMethod] 标注的接口函数 {_method_infos [i].Name} 返回类型非 Task");
						}

						// 将回调函数与返回类型函数
						var _deg_func = _method_infos [i].GetCustomAttribute<ServiceDegradationAttribute> ()?.DegradationFunc;
						_deg_funcs.Add (_deg_func);
						_return_types.Add (_method_infos [i].ReturnType);
						_add_transcall_method (_name, _type_builder, _method_infos [i], _field_deg_funcs, _field_return_types, i);
					}

					// 处理类型
					var _impl_type = _type_builder.CreateType ();
					_impl_o = Activator.CreateInstance (_impl_type);
					_impl_type.InvokeMember ("_faw_field__deg_funcs_", BindingFlags.SetField, null, _impl_o, new [] { _deg_funcs });
					_impl_type.InvokeMember ("_faw_field__return_types_", BindingFlags.SetField, null, _impl_o, new [] { _return_types });
				}

				// 添加进处理对象
				Singletons.InterfaceMap.Add (_type, _impl_o);
			}
		}

		// 创建中转函数
		private static void _add_transcall_method (string _name, TypeBuilder _type_builder, MethodInfo _method_info, FieldBuilder _field_deg_funcs, FieldBuilder _field_return_types, int _index) {
			var _method_attr = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual;
			var _param_types = (from p in _method_info.GetParameters () select p.ParameterType).ToArray ();
			var _method_builder = _type_builder.DefineMethod (_method_info.Name, _method_attr, CallingConventions.Standard, _method_info.ReturnType, _param_types);
			_type_builder.DefineMethodOverride (_method_builder, _method_info);
			var _code = _method_builder.GetILGenerator ();

			// 参数1：参数列表
			var _map = _code.DeclareLocal (typeof (Dictionary<string, object>));
			_code.Emit (OpCodes.Newobj, typeof (Dictionary<string, object>).GetConstructor (Type.EmptyTypes));
			_code.Emit (OpCodes.Stloc, _map);
			var _param_add = typeof(IDictionary<string, object>).GetMethod("Add", new Type[] { typeof(string), typeof(object) });
			var _param_infos = _method_info.GetParameters ();
			int _param_hash = _method_info.GetHashCode ();
			for (int i = 0; i < _param_infos.Length; ++i) {
				_code.Emit (OpCodes.Ldloc, _map);
				_code.Emit (OpCodes.Ldstr, _param_infos [i].Name);
				_emit_load_arg (_code, i);
				if (_param_infos [i].ParameterType.IsValueType)
					_code.Emit (OpCodes.Box, _param_infos [i].ParameterType);
				_code.EmitCall (OpCodes.Callvirt, _param_add, new Type [] { typeof (string), typeof (object) });
				_param_hash ^= _param_infos [i].ParameterType.GetHashCode ();
			}

			// 参数2：降级函数
			var _deg_func = _code.DeclareLocal (typeof (Func<Dictionary<string, object>, Type, object>));
			_code.Emit (OpCodes.Ldarg_0);
			_code.Emit (OpCodes.Ldfld, _field_deg_funcs);
			_emit_fast_int (_code, _index);
			var _list_get_Item_deg_func = typeof (List<Func<Dictionary<string, object>, Type, object>>).GetMethod ("get_Item");
			_code.EmitCall (OpCodes.Callvirt, _list_get_Item_deg_func, new Type [] { typeof (int) });
			_code.Emit (OpCodes.Stloc, _deg_func);

			// 参数3：返回类型
			var _return_type = _code.DeclareLocal (typeof (Type));
			_code.Emit (OpCodes.Ldarg_0);
			_code.Emit (OpCodes.Ldfld, _field_return_types);
			_emit_fast_int (_code, _index);
			var _list_get_Item_return_type = typeof (List<Type>).GetMethod ("get_Item");
			_code.EmitCall (OpCodes.Callvirt, _list_get_Item_return_type, new Type [] { typeof (int) });
			_code.Emit (OpCodes.Stloc, _return_type);

			// 转发请求并返回
			_code.Emit (OpCodes.Ldstr, $"{_name}_{_param_hash.GetHashCode ()}");
			_code.Emit (OpCodes.Ldloc, _map);
			_code.Emit (OpCodes.Ldloc, _deg_func);
			_code.Emit (OpCodes.Ldloc, _return_type);
			var _impl_invoke_method = typeof (ImplCaller).GetMethod ("invoke_method");
			_code.EmitCall (OpCodes.Call, _impl_invoke_method, new Type [] { typeof (string), typeof (Dictionary<string, object>), typeof (Func<Dictionary<string, object>, Type, object>), typeof (Type) });
			_code.Emit (OpCodes.Ret);
		}

		// 创建只读属性
		private static void _add_readonly_property (TypeBuilder _type_builder, string _prop_name, string _prop_value) {
			var _method_attr = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.Virtual;
			var _method_builder = _type_builder.DefineMethod ($"get_{_prop_name}" + _prop_name, _method_attr, typeof (string), Type.EmptyTypes);
			var _code = _method_builder.GetILGenerator ();
			if (_prop_value == null) {
				_code.Emit (OpCodes.Ldnull);
			} else {
				_code.Emit (OpCodes.Ldstr, _prop_value);
			}
			_code.Emit (OpCodes.Ret);
			//
			var _prop_builder = _type_builder.DefineProperty (_prop_name, PropertyAttributes.None, typeof (string), Type.EmptyTypes);
			_prop_builder.SetGetMethod (_method_builder);
		}

		private static void _emit_load_arg (ILGenerator _code, int _index) {
			if (_index == 0) {
				_code.Emit (OpCodes.Ldarg_1);
			} else if (_index == 1) {
				_code.Emit (OpCodes.Ldarg_2);
			} else if (_index == 2) {
				_code.Emit (OpCodes.Ldarg_3);
			} else if (_index <= 126) {
				_code.Emit (OpCodes.Ldarg_S, (byte) _index + 1);
			} else {
				_code.Emit (OpCodes.Ldarg, _index + 1);
			}
		}

		private static void _emit_fast_int (ILGenerator _code, int _value) {
			if (_value >= -1 && _value <= 8) {
				switch (_value) {
					case -1:	_code.Emit (OpCodes.Ldc_I4_M1);		break;
					case 0:		_code.Emit (OpCodes.Ldc_I4_0);		break;
					case 1:		_code.Emit (OpCodes.Ldc_I4_1);		break;
					case 2:		_code.Emit (OpCodes.Ldc_I4_2);		break;
					case 3:		_code.Emit (OpCodes.Ldc_I4_3);		break;
					case 4:		_code.Emit (OpCodes.Ldc_I4_4);		break;
					case 5:		_code.Emit (OpCodes.Ldc_I4_5);		break;
					case 6:		_code.Emit (OpCodes.Ldc_I4_6);		break;
					case 7:		_code.Emit (OpCodes.Ldc_I4_7);		break;
					case 8:		_code.Emit (OpCodes.Ldc_I4_8);		break;
				};
			} else if (_value >= -128 && _value <= 127) {
				_code.Emit (OpCodes.Ldc_I4_S, (sbyte) _value);
			} else {
				_code.Emit (OpCodes.Ldc_I4, _value);
			}
		}
	}
}
