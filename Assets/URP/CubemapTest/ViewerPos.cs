using UnityEngine;

[ExecuteInEditMode]
public class ViewerPos : MonoBehaviour
{
    static Transform viewer;

    public static Vector3 Position
    {
        get
        {
            if (viewer == null)
                return Vector3.zero;
            return viewer.position;
        }
    }

    private void Awake()
    {
        viewer = transform;;
    }
}
