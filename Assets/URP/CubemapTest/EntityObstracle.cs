using Unity.Entities;
using Unity.Rendering;
using UnityEngine;

[DisallowMultipleComponent]
public class EntityObstracle : MonoBehaviour, IConvertGameObjectToEntity
{
    static int id = 5;
    public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
    {
        var component = new URPMaterialPropertyEntityId() { Value = id++ };
        dstManager.AddComponentData(entity, component);
       // id += 5;
    }
}
