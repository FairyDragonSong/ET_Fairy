using System;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace ET
{
    [TypeDrawer]
    public class Float3TypeDrawer: ITypeDrawer
    {
        public bool HandlesType(Type type)
        {
            return type == typeof (float3);
        }

        public object DrawAndGetNewValue(Type memberType, string memberName, object value, object target)
        {
            float3 floatValue = (float3)value;
            Vector3 vectorValue = EditorGUILayout.Vector3Field(memberName, new Vector3(floatValue.x, floatValue.y, floatValue.z));
            return new float3(vectorValue.x, vectorValue.y, vectorValue.z);
        }
    }
}
