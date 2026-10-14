//! The enumeration order of a .NET `HashSet<T>`, which decides the file the arena and blob managers pick from their
//! pools of files with room, and so where every table and blob run lands.

/// A set enumerated as .NET's `HashSet<T>` is: entries in slot order, where an add reuses the slot freed last.
pub struct PoolSet<T> {
    slots: Vec<Option<T>>,
    free: Vec<usize>,
}

impl<T> Default for PoolSet<T> {
    fn default() -> Self {
        Self {
            slots: Vec::new(),
            free: Vec::new(),
        }
    }
}

impl<T: Copy + Eq> PoolSet<T> {
    pub fn add(&mut self, item: T) {
        if self.contains(item) {
            return;
        }
        match self.free.pop() {
            Some(slot) => self.slots[slot] = Some(item),
            None => self.slots.push(Some(item)),
        }
    }

    pub fn remove(&mut self, item: T) {
        if let Some(slot) = self.slots.iter().position(|entry| *entry == Some(item)) {
            self.slots[slot] = None;
            self.free.push(slot);
        }
    }

    pub fn contains(&self, item: T) -> bool {
        self.slots.contains(&Some(item))
    }

    /// The items in enumeration order.
    pub fn items(&self) -> Vec<T> {
        self.slots.iter().flatten().copied().collect()
    }
}

#[cfg(test)]
mod tests {
    use super::PoolSet;

    #[test]
    fn adds_reuse_the_slot_freed_last() {
        let mut set = PoolSet::default();
        for item in [1, 2, 3, 4] {
            set.add(item);
        }
        set.add(2);
        set.remove(2);
        set.remove(4);
        set.remove(9);
        assert_eq!(set.items(), [1, 3]);

        set.add(5);
        set.add(6);
        set.add(7);
        assert_eq!(set.items(), [1, 6, 3, 5, 7], "5 takes 4's slot, 6 takes 2's, 7 appends");
        assert!(set.contains(6) && !set.contains(2));
    }
}
