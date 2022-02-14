using Unity.Entities;
using Unity.Mathematics;

#if ENABLE_HYBRID_RENDERER_V2
namespace Unity.Rendering
{
    [MaterialProperty("_EntityID", MaterialPropertyFormat.Float)]
    public struct URPMaterialPropertyEntityId : IComponentData
    {
        public float Value;
    }
}
#endif