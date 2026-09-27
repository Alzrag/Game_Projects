using UnityEngine;

/// <summary>
/// Attach this to your bulletPrefab. Blaster.cs calls Initialize() right after
/// spawning it, which is what actually gives the bolt its speed and damage.
/// </summary>
public class Lazers : MonoBehaviour
{
    [Header("Runtime Stats (set by Blaster.Initialize)")]
    public float speed = 50f;
    public float damage = 10f;

    // The direction the bolt actually travels. Kept separate from transform.forward
    // because the mesh may be rotated (see Blaster.projectileRotationOffset) purely
    // to make it look right, which would otherwise throw off movement too.
    private Vector3 moveDirection;

    [Header("Lifetime")]
    [Tooltip("Bolt is destroyed automatically after this many seconds if it hits nothing.")]
    public float lifeTime = 5f;

    [Header("Collision")]
    [Tooltip("Which layers this bolt can hit. Leave as Everything if unsure.")]
    public LayerMask hitMask = ~0;

    [Tooltip("Destroy the bolt on any hit, even if the thing it hit has no health component.")]
    public bool destroyOnAnyHit = true;

    [Header("Effects (optional)")]
    public GameObject impactEffectPrefab;

    private Vector3 previousPosition;
    private bool hasBeenInitialized;

    /// <summary>
    /// Called by Blaster.Shoot() right after Instantiate. Sets the bolt's
    /// speed/damage and resets its lifetime clock. Pass the intended travel
    /// direction explicitly so it isn't affected by any visual mesh rotation
    /// offset applied to this object.
    /// </summary>
    public void Initialize(float speedValue, float damageValue, Vector3 direction)
    {
        speed = speedValue;
        damage = damageValue;
        moveDirection = direction.normalized;
        hasBeenInitialized = true;
    }

    /// <summary>
    /// Backward-compatible overload: falls back to transform.forward if no
    /// direction is supplied.
    /// </summary>
    public void Initialize(float speedValue, float damageValue)
    {
        Initialize(speedValue, damageValue, transform.forward);
    }

    private void Start()
    {
        previousPosition = transform.position;
        Destroy(gameObject, lifeTime);
    }

    private void Update()
    {
        // Move forward every frame, using the direction set at spawn time
        // rather than transform.forward (which may be rotated purely for visuals).
        transform.position += moveDirection * speed * Time.deltaTime;

        // Raycast from where we were to where we are now, rather than relying
        // on physics collision, so fast bolts don't tunnel through thin
        // colliders between frames.
        Vector3 currentPosition = transform.position;
        Vector3 delta = currentPosition - previousPosition;
        float distance = delta.magnitude;

        if (distance > 0f)
        {
            if (Physics.Raycast(previousPosition, delta.normalized, out RaycastHit hit, distance, hitMask, QueryTriggerInteraction.Ignore))
            {
                HandleHit(hit);
            }
        }

        previousPosition = currentPosition;
    }

    private void HandleHit(RaycastHit hit)
    {
        // Snap the bolt to the exact impact point before doing anything else,
        // so effects and destruction look right.
        transform.position = hit.point;

        // Try to damage whatever we hit, if it exposes a compatible interface/component.
        IDamageable damageable = hit.collider.GetComponentInParent<IDamageable>();
        if (damageable != null)
        {
            damageable.TakeDamage(damage);
        }
        else if (!destroyOnAnyHit)
        {
            // Hit something with no health component and we're configured to
            // ignore those hits (e.g. pass through decorative geometry).
            return;
        }

        SpawnImpactEffect(hit.point, hit.normal);
        Destroy(gameObject);
    }

    private void SpawnImpactEffect(Vector3 position, Vector3 normal)
    {
        if (impactEffectPrefab == null) return;

        Instantiate(impactEffectPrefab, position, Quaternion.LookRotation(normal));
    }
}

/// <summary>
/// Implement this on anything that should be able to take damage from a bolt
/// (enemies, destructible props, the player, etc).
/// </summary>
public interface IDamageable
{
    void TakeDamage(float amount);
}